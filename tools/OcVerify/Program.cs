using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;

namespace OcVerify
{
    /// <summary>
    /// Phase-2 hardware verification (spec section 9). Runs the REAL services
    /// (NvApiGpuController, SafetyRevertService, telemetry polling) against the
    /// machine's actual NVIDIA GPU. No fakes anywhere in this program.
    ///
    /// Usage:  dotnet run --project tools/OcVerify [-p:Platform=x64] [step]
    /// Steps:  telemetry (read-only), cycle (apply/confirm/revert + range probe).
    /// Default runs telemetry then cycle.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }

        private static int Main(string[] args)
        {
            Console.WriteLine("=== Phase-2 hardware verification (REAL NVAPI path) ===");
            Console.WriteLine("Machine: " + Environment.MachineName + ", elevated: " + IsElevated());
            Console.WriteLine();

            // Child mode: acts as the "app running curve mode" that the orphan
            // test hard-kills. Runs its own fan-hold loop and exits by being
            // killed - never cleans up, exactly like a crashed app.
            if (args.FirstOrDefault() == "fan-hold")
            {
                RunFanHoldChild(args.Length > 1 ? int.Parse(args[1]) : 12);
                return 0;
            }

            var step = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "all";
            if (step is "telemetry" or "all") VerifyTelemetry();
            if (step is "cycle" or "all") VerifySafetyCycle();
            if (step is "tdr") VerifyTdrWatchdog();
            if (step is "watchdog") VerifyWatchdogPipeline();
            if (step is "fan") VerifyFanCurveUnderLoad();
            if (step is "orphan") VerifyCrashOrphanRecovery();
            if (step is "limits") VerifyLimitsSafetyPath();
            if (step is "startup") VerifyStartupTask();
            if (step is "startup-apply") VerifyStartupApplyFlow();
            if (step is "logic") _failures += VfLogicChecks.Run();
            if (step is "vf") VerifyVfCurve();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }

        private static bool IsElevated()
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // ---------------------------------------------------------------
        // Step 1 (spec 9): telemetry matches nvidia-smi within tolerance.
        // ---------------------------------------------------------------
        private static void VerifyTelemetry()
        {
            Console.WriteLine("--- Telemetry vs nvidia-smi (5 samples, 1s apart) ---");

            var controller = new NvApiGpuController();
            var init = controller.Initialize();
            if (!init.IsSuccess)
            {
                Check(false, $"NVAPI initialize: {init.ErrorKind} {init.Detail}");
                return;
            }

            var id = controller.GetIdentity();
            Check(id.IsSuccess, $"GPU identity: {(id.IsSuccess ? $"{id.Value.FullName}, driver {id.Value.DriverVersion}" : init.Detail)}");
            if (!id.IsSuccess) return;

            // Grab an nvidia-smi line for the cross-check.
            var smi = QuerySmi();
            if (smi.HasValue)
            {
                var (name, ver, temp, core, mem) = smi.Value;
                Console.WriteLine($"  nvidia-smi: {name}, driver {ver}, temp {temp}C, core {core}MHz, mem {mem}MHz");
            }
            else
            {
                Console.WriteLine("  (nvidia-smi not available for cross-check)");
            }

            int goodReads = 0;
            double worstCoreDelta = 0, worstMemDelta = 0;
            int? worstTempDelta = null;

            for (int i = 0; i < 5; i++)
            {
                var t = controller.ReadTelemetry();
                if (!t.IsSuccess || t.Value is not { } s)
                {
                    Check(false, $"telemetry read {i + 1}: {t.ErrorKind} {t.Detail}");
                    continue;
                }
                goodReads++;
                Console.WriteLine(
                    $"  read {i + 1}: core {Fmt(s.CoreClockMHz)} MHz, mem {Fmt(s.MemClockMHz)} MHz, temp {Fmt(s.GpuTempC)}C, " +
                    $"hotspot {Fmt(s.HotspotTempC)}, volt {Fmt(s.VoltageMv)} mV, power {Fmt(s.PowerDrawW)} W, " +
                    $"fan {Fmt(s.FanPercent)}%/{Fmt(s.FanRpm)}rpm, usage {Fmt(s.GpuUsagePercent)}%, " +
                    $"vram {Fmt(s.VramUsageMb)} MB, pcie Gen{Fmt(s.PcieGen)} x{Fmt(s.PcieWidth)}");

                if (smi.HasValue)
                {
                    if (s.CoreClockMHz is { } c) worstCoreDelta = Math.Max(worstCoreDelta, Math.Abs(c - smi.Value.Core));
                    if (s.MemClockMHz is { } m) worstMemDelta = Math.Max(worstMemDelta, Math.Abs(m - smi.Value.Mem));
                    if (s.GpuTempC is { } tp)
                    {
                        int d = Math.Abs(tp - smi.Value.Temp);
                        worstTempDelta = worstTempDelta is null ? d : Math.Max(worstTempDelta.Value, d);
                    }
                }

                Thread.Sleep(1000);
            }

            Check(goodReads == 5, $"5/5 telemetry reads succeeded");
            if (smi.HasValue && goodReads > 0)
            {
                // Idle clocks bounce between P-states; sample-to-sample deltas are
                // expected. Tolerances cover P-state transitions during the window.
                Check(worstTempDelta is null || worstTempDelta <= 3, $"GPU temp within 3C of nvidia-smi (worst delta {worstTempDelta}C)");
                Check(worstCoreDelta <= 400, $"core clock within 400MHz of nvidia-smi (worst delta {worstCoreDelta:0}MHz - P-state bounce)");
                Check(worstMemDelta <= 400, $"mem clock within 400MHz of nvidia-smi (worst delta {worstMemDelta:0}MHz - P-state bounce)");
            }

            // Capabilities: ranges must be driver-queried, never hardcoded.
            var caps = controller.ReadCapabilities();
            if (caps.IsSuccess && caps.Value is { } cv)
            {
                Console.WriteLine(
                    $"  caps: core [{cv.CoreOffsetRangeMHz?.Minimum}..{cv.CoreOffsetRangeMHz?.Maximum}] MHz, " +
                    $"mem [{cv.MemOffsetRangeMHz?.Minimum}..{cv.MemOffsetRangeMHz?.Maximum}] MHz, " +
                    $"power [{cv.PowerLimitRangePercent?.Minimum}..{cv.PowerLimitRangePercent?.Maximum}]%, " +
                    $"temp [{cv.TempLimitRangeC?.Minimum}..{cv.TempLimitRangeC?.Maximum}]C, fan={cv.FanControlSupported}");
                Check(cv.CoreOffsetRangeMHz is not null, "core offset range present (RTX 40-series expects ~[-200..+200])");
                Check(cv.MemOffsetRangeMHz is not null, "memory offset range present");
                Check(cv.PowerLimitRangePercent is not null, "power limit range present");
            }
            else
            {
                Check(false, $"ReadCapabilities failed: {caps.ErrorKind} {caps.Detail}");
            }
        }

        private static string Fmt(double? v) => v?.ToString("0.#") ?? "n/a";
        private static string Fmt(int? v) => v?.ToString() ?? "n/a";

        private static (string Name, string Ver, int Temp, int Core, int Mem)? QuerySmi()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("nvidia-smi",
                    "--query-gpu=name,driver_version,temperature.gpu,clocks.gr,clocks.mem --format=csv,noheader")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(5000);
                if (string.IsNullOrEmpty(line))
                {
                    Console.WriteLine("  (nvidia-smi produced no output: " + p.StandardError.ReadToEnd().Trim() + ")");
                    return null;
                }
                var parts = line.Split(',');
                if (parts.Length < 5) return null;
                // CSV cells carry units ("210 MHz", "42"): keep digits/sign only.
                static int Num(string cell)
                {
                    var digits = new string(cell.Trim().TakeWhile(ch => char.IsDigit(ch) || ch == '-').ToArray());
                    return int.Parse(digits);
                }
                return (parts[0].Trim(), parts[1].Trim(), Num(parts[2]), Num(parts[3]), Num(parts[4]));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  (nvidia-smi spawn failed: " + ex.Message + ")");
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Step 2 (spec 9): the section-4 safety machine, end to end, on
        // the real GPU - apply/confirm, apply/expire-revert, manual revert.
        // ---------------------------------------------------------------
        private static void VerifySafetyCycle()
        {
            Console.WriteLine("--- Safety state machine end-to-end on real hardware ---");

            var controller = new NvApiGpuController();
            var init = controller.Initialize();
            if (!init.IsSuccess) { Check(false, "NVAPI init"); return; }

            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log) { ConfirmationSeconds = 3 };

            var stateLog = new System.Collections.Generic.List<string>();
            safety.StateChanged += (_, e) => stateLog.Add($"{e.State}({e.CountdownSecondsRemaining})");

            // Baseline must be the pre-batch anchor for every test below.
            var baseOff = controller.ReadCurrentOffsets();
            var baseLim = controller.ReadCurrentLimits();
            if (!baseOff.IsSuccess || !baseLim.IsSuccess)
            {
                Check(false, "read baseline offsets/limits");
                return;
            }
            int core0 = baseOff.Value.CoreOffsetMHz;
            int mem0 = baseOff.Value.MemOffsetMHz;
            double power0 = baseLim.Value.PowerLimitPercent;
            Console.WriteLine($"  baseline: core {core0:+0;-0;0} MHz, mem {mem0:+0;-0;0} MHz, power {power0:0.#}%");

            // Readback sanity: what the driver reports must match what we'll use as anchor.
            var offNow = controller.ReadCurrentOffsets();
            Check(offNow.IsSuccess && Math.Abs(offNow.Value.CoreOffsetMHz - core0) == 0, "offset readback is stable across reads");

            try
            {
                // ---- Test A: apply + confirm -> value sticks ------------------
                var a = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.CoreClockOffset, () => controller.SetCoreOffsetMhz(core0 + 15), "baseline", "+15 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(a, "A: batch applied (driver accepted +15 MHz core)");
                Check(safety.CurrentState == SafetyState.AwaitingConfirmation, "A: entered AwaitingConfirmation");

                Thread.Sleep(1500); // readback during the window
                var readA = controller.ReadCurrentOffsets();
                Check(readA.IsSuccess && readA.Value.CoreOffsetMHz == core0 + 15,
                    $"A: readback confirms +15 (read {readA.Value.CoreOffsetMHz:+0;-0;0}, want {core0 + 15:+0;-0;0})");

                safety.Confirm();
                Check(safety.CurrentState == SafetyState.Idle, "A: confirm -> Idle");
                Check(controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0 + 15, "A: value sticks after confirm");

                // ---- Test B: apply + let the countdown expire -> auto revert --
                var b = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.CoreClockOffset, () => controller.SetCoreOffsetMhz(core0 - 15), "+15", "-15 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(b, "B: batch applied (driver accepted -15 MHz core)");
                Check(controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0 - 15, "B: -15 visible during window");

                // The anchor is Test A's CONFIRMED +15 - last known good, not
                // factory 0 (spec 4.2: revert restores prior working config).
                bool revertedB = SpinUntil(() => safety.CurrentState == SafetyState.Idle &&
                                                controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0 + 15, 8000);
                Check(revertedB, "B: countdown expiry reverted to the last-known-good (+15), not factory defaults");
                Check(stateLog.Any(s => s.StartsWith("Reverting")), "B: passed through Reverting state");

                // ---- Test C: manual revert mid-window --------------------------
                var c = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.MemoryClockOffset, () => controller.SetMemoryOffsetMhz(mem0 + 100), "baseline", "+100 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(c, "C: mem batch applied");
                Check(controller.ReadCurrentOffsets().Value.MemOffsetMHz == mem0 + 100, "C: +100 mem visible during window");
                safety.RevertNow();
                bool revertedC = SpinUntil(() => safety.CurrentState == SafetyState.Idle &&
                                                controller.ReadCurrentOffsets().Value.MemOffsetMHz == mem0, 5000);
                Check(revertedC, "C: manual revert restored the anchor");

                // ---- Test D: out-of-range probe -------------------------------
                // Hardware finding: the driver ACCEPTS a +10000 MHz delta without
                // error at the API level, so the controller must clamp client-side
                // to the driver-queried range (defense-in-depth for profile applies).
                var oor = controller.SetCoreOffsetMhz(10000);
                var rangeMax = controller.ReadCapabilities().Value.CoreOffsetRangeMHz?.Maximum ?? 0;
                var oorRead = controller.ReadCurrentOffsets().Value.CoreOffsetMHz;
                Check(oor.IsSuccess && oorRead <= rangeMax,
                    $"D: out-of-range write clamped to queried range (wrote +10000, applied {oorRead:+0;-0;0}, max {rangeMax:+0;-0;0})");
                controller.SetCoreOffsetMhz(core0); // restore immediately - do not hold the clamped max offset

                // ---- Test E: revert-to-prior-config (not defaults) ------------
                // Apply + confirm a +10 working config, then a second batch that
                // expires; revert must restore the +10, not 0.
                safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.CoreClockOffset, () => controller.SetCoreOffsetMhz(core0 + 10), "base", "+10"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                safety.Confirm();
                var anchor2 = controller.ReadCurrentOffsets().Value.CoreOffsetMHz;

                safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.CoreClockOffset, () => controller.SetCoreOffsetMhz(anchor2 + 20), "+10", "+30"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                safety.RevertNow();
                bool back2 = SpinUntil(() => safety.CurrentState == SafetyState.Idle &&
                                          controller.ReadCurrentOffsets().Value.CoreOffsetMHz == anchor2, 5000);
                Check(back2, $"E: revert restored prior working config (+10), not defaults");

                // ---- Restore baseline & disarm ---------------------------------
                safety.Confirm(); // in case a window is somehow open
                watchdog.Disarm();
                var rest = controller.SetCoreOffsetMhz(core0);
                controller.SetMemoryOffsetMhz(mem0);
                controller.SetPowerLimitPercent(power0);
                Check(rest.IsSuccess && controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0, "final: baseline restored");
            }
            finally
            {
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
            }

            Console.WriteLine();
            Console.WriteLine("Change-log entries written this run:");
            foreach (var e in logger.Tail().TakeLast(12))
                Console.WriteLine("  " + e.Result + "  " + e.ControlName + ": " + e.OldValue + " -> " + e.NewValue + "  (" + e.Source + ")" + (e.FailureReason is null ? "" : " - " + e.FailureReason));
        }

        // ---------------------------------------------------------------
        // Step 5 (spec 9): fan Curve mode tracks a real thermal change. No
        // load tools exist on this machine, so the load is induced with a
        // driver clock lock (nvidia-smi -lgc), which holds the GPU at boost
        // clock and roughly doubles idle power draw - a genuine thermal event
        // the curve must react to. Fully revertible with -rgc.
        // ---------------------------------------------------------------
        private static void VerifyFanCurveUnderLoad()
        {
            Console.WriteLine("--- Fan Curve mode under a real thermal change (clock-lock load) ---");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }

            // Never fight a live curve loop from the real app.
            if (System.IO.File.Exists(FanCurveExecutionService.GetMarkerPath(null)))
            {
                Console.WriteLine("  active curve marker present - aborting to avoid fighting a live session");
                Check(false, "fan test precondition (no live curve)");
                return;
            }

            var baseline = controller.ReadTelemetry();
            if (!baseline.IsSuccess) { Check(false, "baseline telemetry"); return; }
            double idlePower = baseline.Value.PowerDrawW ?? 0;
            int? idleFan = baseline.Value.FanPercent;
            Console.WriteLine($"  idle: {Fmt(baseline.Value.GpuTempC)}C, {idlePower:0.#} W, fan {Fmt(idleFan)}%");

            var safety = new FanCurveExecutionService(controller);
            bool stopped = false;
            safety.Stopped += _ => stopped = true;
            try
            {
                // Real load: lock boost clock so the GPU leaves idle P-states.
                if (!RunSmi("-lgc 2400,2400")) { Check(false, "clock lock (nvidia-smi -lgc)"); return; }
                Thread.Sleep(4000);
                var loaded = controller.ReadTelemetry();
                double loadPower = loaded.Value.PowerDrawW ?? 0;
                Check(loadPower >= idlePower * 1.4,
                    $"clock lock induced a real thermal load ({idlePower:0.#} W -> {loadPower:0.#} W, {Fmt(loaded.Value.GpuTempC)}C)");

                // Curve that spans the idle band: at ~43C it commands ~60%, far
                // above the idle 0%. The loop must track it within its interval.
                var points = new[]
                {
                    new FanCurvePoint(35, 20),
                    new FanCurvePoint(45, 60),
                    new FanCurvePoint(55, 85),
                    new FanCurvePoint(100, 100),
                };
                safety.Start(points, () => controller.ReadTelemetry().Value?.GpuTempC);
                safety.Interval = TimeSpan.FromSeconds(1);

                int? peakFan = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20_000)
                {
                    Thread.Sleep(1000);
                    var t = controller.ReadTelemetry();
                    if (t.IsSuccess && t.Value.FanPercent is { } f)
                    {
                        peakFan = peakFan is null ? f : Math.Max(peakFan.Value, f);
                        Console.WriteLine($"  t+{sw.ElapsedMilliseconds / 1000,2}s: temp {Fmt(t.Value.GpuTempC)}C, fan {f}% (curve commands ~{FanCurveExecutionService.Evaluate(points, t.Value.GpuTempC ?? 0)}%)");
                    }
                }

                Check(peakFan is > 0,
                    $"fan responded to the curve under load (peak {peakFan ?? -1}% vs idle {Fmt(idleFan)}%)");
                Check(peakFan >= 40,
                    $"fan tracked the commanded range meaningfully (peak {peakFan ?? -1}%, curve ~60% at this temp)");
            }
            finally
            {
                RunSmi("-rgc"); // release the clock lock no matter what
                if (!stopped) safety.StopAsync("harness teardown").GetAwaiter().GetResult();

                // RestoreFanAuto returns before the driver's auto loop ramps the
                // physical fan down from the last forced level - give it a
                // settle window rather than sampling a single instant.
                var fanFree = SpinUntil(() =>
                {
                    var t = controller.ReadTelemetry();
                    return t.IsSuccess && (t.Value.FanPercent is null || t.Value.FanPercent <= (idleFan ?? 0) + 5);
                }, 12_000);
                var after = controller.ReadTelemetry();
                Check(fanFree,
                    $"fan handed back to driver control after stop (now {Fmt(after.Value.FanPercent)}%)");
                var post = controller.ReadTelemetry();
                Console.WriteLine($"  after teardown: {Fmt(post.Value.GpuTempC)}C, {post.Value.PowerDrawW ?? 0:0.#} W (clock lock released)");
            }
        }

        private static bool RunSmi(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("nvidia-smi", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                if (p.ExitCode != 0)
                    Console.WriteLine("  nvidia-smi " + args + " failed: " + output.Trim());
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  nvidia-smi spawn failed: " + ex.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // Step 6 (spec 9): kill the process mid-Curve-mode; the fan must not
        // stay stuck. A hard kill cannot run Dispose, so this exercises the
        // liveness-marker + EnsureNoOrphanedFanControl recovery path with a
        // genuinely killed child process (the "app" being tested).
        // ---------------------------------------------------------------
        private static void VerifyCrashOrphanRecovery()
        {
            Console.WriteLine("--- Hard-kill mid-Curve-mode: fan must not stay stuck ---");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }

            if (System.IO.File.Exists(FanCurveExecutionService.GetMarkerPath(null)))
            {
                Console.WriteLine("  active curve marker present - aborting to avoid fighting a live session");
                Check(false, "orphan test precondition (no live curve)");
                return;
            }

            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "OcVerify.exe");
            if (!System.IO.File.Exists(exe)) { Check(false, "harness exe for child spawn"); return; }

            // Spawn the "app": a child that forces the fan to 100% via the REAL
            // curve service and the REAL marker path, then sits until killed.
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "fan-hold 60")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var child = System.Diagnostics.Process.Start(psi)!;
            string? line;
            var ready = new System.Diagnostics.Stopwatch();
            ready.Start();
            while ((line = child.StandardOutput.ReadLine()) != null)
            {
                Console.WriteLine("  [child] " + line);
                if (line == "READY" || ready.ElapsedMilliseconds > 15_000) break;
            }
            Thread.Sleep(1500); // first forced write lands (interval 500ms)

            child.Kill(entireProcessTree: true); // THE hard kill - no Dispose possible
            child.WaitForExit(5000);
            Check(child.HasExited, "child process killed mid-Curve (no cleanup ran)");

            var markerPath = FanCurveExecutionService.GetMarkerPath(null);
            Check(System.IO.File.Exists(markerPath), "liveness marker survived the kill (orphan state present)");

            var stuck = controller.ReadTelemetry();
            Console.WriteLine($"  fan while orphaned: {Fmt(stuck.Value.FanPercent)}% (forced 100%)");

            // Recovery - the path a fresh app start takes.
            bool recovered = FanCurveExecutionService.EnsureNoOrphanedFanControl(controller);
            Check(recovered, "startup recovery detected the orphan and restored driver control");
            Check(!System.IO.File.Exists(markerPath), "marker cleared after recovery");

            bool fanFree = SpinUntil(() =>
            {
                var t = controller.ReadTelemetry();
                return t.IsSuccess && (t.Value.FanPercent is null || t.Value.FanPercent < 60);
            }, 15_000);
            Check(fanFree, $"fan no longer stuck (driver auto took over, now {Fmt(controller.ReadTelemetry().Value.FanPercent)}%)");

            Check(!FanCurveExecutionService.EnsureNoOrphanedFanControl(controller),
                "recovery is a no-op when no orphan exists");
        }

        // ---------------------------------------------------------------
        // Phase 3 (spec process step 3): the REMAINING controls - power limit
        // and temp limit - through the same safety machine, with driver
        // readback confirmation (taxonomy: "write succeeded but readback does
        // not confirm the change" must surface, not silently pass).
        // ---------------------------------------------------------------
        private static void VerifyLimitsSafetyPath()
        {
            Console.WriteLine("--- Power + temp limits through the safety machine (real driver) ---");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }
            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log) { ConfirmationSeconds = 3 };

            var caps = controller.ReadCapabilities();
            if (!caps.IsSuccess) { Check(false, "capabilities"); return; }
            var powerRange = caps.Value.PowerLimitRangePercent;
            var tempRange = caps.Value.TempLimitRangeC;
            Console.WriteLine($"  ranges: power [{powerRange?.Minimum}..{powerRange?.Maximum}]%, temp [{tempRange?.Minimum}..{tempRange?.Maximum}]C");

            var baseLim = controller.ReadCurrentLimits();
            if (!baseLim.IsSuccess) { Check(false, "baseline limits"); return; }
            double power0 = baseLim.Value.PowerLimitPercent;
            int? temp0 = baseLim.Value.TempLimitC;
            Console.WriteLine($"  baseline: power {power0:0.#}%, temp {(temp0 is null ? "n/a" : temp0 + " C")}");

            try
            {
                if (powerRange is null) { Check(false, "power limit control exposed by driver"); return; }

                // Values stay modest and inside the queried range: +10% power
                // (idle card draws ~17W - no thermal risk in a 3s window) and
                // temp limit +2C. The controller clamps regardless.
                double powerNew = Math.Min(powerRange!.Maximum, power0 + 10);

                // ---- Test P: power limit apply/confirm/sticks -----------------
                var p = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.PowerLimit, () => controller.SetPowerLimitPercent(powerNew), $"{power0:0.#}%", $"{powerNew:0.#}%"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(p, "P: power-limit batch applied");

                var readP = controller.ReadCurrentLimits();
                Check(readP.IsSuccess && Math.Abs(readP.Value.PowerLimitPercent - powerNew) < 0.5,
                    $"P: driver readback confirms power limit (read {readP.Value.PowerLimitPercent:0.#}%, want {powerNew:0.#}%)");

                safety.Confirm();
                var stickP = controller.ReadCurrentLimits();
                Check(Math.Abs(stickP.Value.PowerLimitPercent - powerNew) < 0.5, "P: power limit sticks after confirm");

                // ---- Test T: temp limit through the same machine --------------
                if (tempRange is { } tr && temp0 is { } t0)
                {
                    int tempNew = (int)Math.Min(tr.Maximum, t0 + 2);
                    var t = safety.ApplyBatchAsync(new[]
                    {
                        new PendingChange(OcControlNames.TemperatureLimit, () => controller.SetTempLimitC(tempNew), $"{t0} °C", $"{tempNew} °C"),
                    }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                    Check(t, "T: temp-limit batch applied");

                    var readT = controller.ReadCurrentLimits();
                    Check(readT.IsSuccess && readT.Value.TempLimitC == tempNew,
                        $"T: driver readback confirms temp limit (read {readT.Value.TempLimitC}, want {tempNew})");

                    // Countdown expiry must restore the confirmed power anchor AND the old temp.
                    bool revertedT = SpinUntil(() => safety.CurrentState == SafetyState.Idle, 8000);
                    var afterT = controller.ReadCurrentLimits();
                    Check(revertedT && afterT.Value.TempLimitC == t0,
                        $"T: expiry reverted temp limit to anchor (read {afterT.Value.TempLimitC}, want {t0})");
                    Check(Math.Abs(afterT.Value.PowerLimitPercent - powerNew) < 0.5,
                        "T: confirmed power anchor untouched by the temp batch revert");
                }
                else
                {
                    Console.WriteLine("  (temp limit not exposed by this driver - control correctly hidden in UI)");
                }

                // ---- Test PW: power batch expiry reverts to confirmed anchor --
                double powerNewer = Math.Max(powerRange!.Minimum, powerNew - 15);
                safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.PowerLimit, () => controller.SetPowerLimitPercent(powerNewer), $"{powerNew:0.#}%", $"{powerNewer:0.#}%"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                bool revertedP = SpinUntil(() => safety.CurrentState == SafetyState.Idle, 8000);
                var afterP = controller.ReadCurrentLimits();
                Check(revertedP && Math.Abs(afterP.Value.PowerLimitPercent - powerNew) < 0.5,
                    $"PW: expiry reverted power to the confirmed anchor (read {afterP.Value.PowerLimitPercent:0.#}%, want {powerNew:0.#}%)");
            }
            finally
            {
                safety.Confirm();
                SpinUntil(() => safety.CurrentState == SafetyState.Idle, 3000);
                controller.SetPowerLimitPercent(power0);
                if (temp0 is { } tRest) controller.SetTempLimitC(tRest);
                var fin = controller.ReadCurrentLimits();
                Check(Math.Abs(fin.Value.PowerLimitPercent - power0) < 0.5, "final: power baseline restored");
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
            }
        }

        // ---------------------------------------------------------------
        // Phase 5 (spec 6): the elevated Task Scheduler task, against the REAL
        // scheduler: register -> query shows it -> unregister -> gone. Uses the
        // real task name, so it must always end unregistered.
        // ---------------------------------------------------------------
        private static void VerifyStartupTask()
        {
            Console.WriteLine("--- Startup task: real Task Scheduler register/unregister cycle ---");
            var svc = new StartupTaskService();

            var wasRegistered = svc.IsRegistered;
            Console.WriteLine($"  pre-existing registration: {wasRegistered}");
            if (wasRegistered)
            {
                Console.WriteLine("  task already registered - verifying query only (not unregistering a real user setting)");
                Check(true, "startup task: registration state queryable");
                return;
            }

            Check(svc.Register(), "register created the elevated logon task");
            Check(svc.IsRegistered, "query sees the task after register");

            Check(svc.Unregister(), "unregister removed the task");
            Check(!svc.IsRegistered, "query no longer sees the task after unregister");
        }

        // ---------------------------------------------------------------
        // Phase 5 end-to-end: the headless startup reapply through the REAL
        // safety machine - designated + validated profile, shortened silent
        // window, expiry-confirms, ConfirmedAt stamped, baseline restored.
        // Uses an isolated profile directory (never touches the real app's
        // default designation).
        // ---------------------------------------------------------------
        private static void VerifyStartupApplyFlow()
        {
            Console.WriteLine("--- Startup reapply flow (real machine, headless window) ---");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }
            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log);

            var profileDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ocverify-profiles-" + Guid.NewGuid().ToString("N"));
            var storage = new ProfileStorageService(profileDir);
            var traces = new System.Collections.Generic.List<string>();
            var startup = new StartupApplyService(controller, safety, storage, logger,
                msg => { traces.Add(msg); Console.WriteLine("  [startup] " + msg); })
            { StartupConfirmationSeconds = 3 };

            var baseOff = controller.ReadCurrentOffsets();
            if (!baseOff.IsSuccess) { Check(false, "read baseline"); return; }
            int core0 = baseOff.Value.CoreOffsetMHz;

            try
            {
                // Not validated -> must be REFUSED (the black-screen guard).
                var unvalidated = new OverclockProfile { Name = "unvalidated", CoreOffsetMHz = core0 + 15 };
                storage.Save(unvalidated);
                storage.SetDefaultProfile("unvalidated");
                startup.ApplyDefaultProfileAtStartupAsync().GetAwaiter().GetResult();
                Check(controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0,
                    "unvalidated profile is refused (no pre-boot-validated flag)");

                // Validated -> applied, silent window expires into confirm.
                var profile = new OverclockProfile { Name = "validated", CoreOffsetMHz = core0 + 15, ConfirmedAt = DateTime.Now };
                storage.Save(profile);
                storage.SetDefaultProfile("validated");

                startup.ApplyDefaultProfileAtStartupAsync().GetAwaiter().GetResult();

                Check(controller.ReadCurrentOffsets().Value.CoreOffsetMHz == core0 + 15,
                    "validated profile reapplied at startup (+15 core)");
                Check(profile.ConfirmedAt is not null, "ConfirmedAt stamped after clean headless window");
            }
            finally
            {
                controller.SetCoreOffsetMhz(core0);
                var fin = controller.ReadCurrentOffsets();
                Check(fin.IsSuccess && fin.Value.CoreOffsetMHz == core0, "final: baseline restored");
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
                try { System.IO.Directory.Delete(profileDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Child mode: forces 100% fan through the REAL curve service (real
        /// marker path), announces READY, then blocks. The parent kills it -
        /// simulating an app crash with a forced fan. Never cleans up.
        /// </summary>
        private static void RunFanHoldChild(int seconds)
        {
            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Console.WriteLine("INIT-FAILED"); return; }
            var curve = new FanCurveExecutionService(controller);
            curve.Interval = TimeSpan.FromMilliseconds(500);
            curve.Start(new[] { new FanCurvePoint(0, 100) }, () => controller.ReadTelemetry().Value?.GpuTempC ?? 50);
            Console.WriteLine("READY");
            Console.Out.Flush();
            Thread.Sleep(seconds * 1000);
            // Intentionally NO StopAsync/Dispose - the kill simulates a crash.
        }

        // ---------------------------------------------------------------
        // Step 4: watchdog detection-pipeline validation. The card survived
        // slider-max offsets (idle clocks never reach stressed states), so a
        // real crash cannot be produced within driver-allowed ranges. This
        // validates everything AROUND the crash instead: the event-log reader,
        // the arm/tick/baseline logic, and the watchdog -> safety-machine
        // revert, using a clearly-labeled synthetic nvlddmkm Error event.
        // ---------------------------------------------------------------
        private static void VerifyWatchdogPipeline()
        {
            Console.WriteLine("--- TDR watchdog detection pipeline (synthetic event-log entry) ---");

            // Raw reader probe: enumerate recent System events so a query or
            // provider-name mismatch is visible instead of swallowed.
            try
            {
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    "*[System[TimeCreated[timediff(@SystemTime) <= 3600000]]]")
                { ReverseDirection = true };
                using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                int shown = 0;
                while (r.ReadEvent() is System.Diagnostics.Eventing.Reader.EventRecord rec && shown < 8)
                {
                    using (rec)
                        Console.WriteLine($"  probe: {rec.TimeCreated:HH:mm:ss} provider={rec.ProviderName} id={rec.Id} level={rec.Level}");
                    shown++;
                }
                Console.WriteLine($"  probe: {shown} recent event(s) visible");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  probe FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Reader check: the historical nvlddmkm error must be findable.
            var last = TdrWatchdogService.TryReadLastTdrTimeUtc();
            Check(last.HasValue, $"event-log reader finds nvlddmkm errors (latest: {last:yyyy-MM-dd HH:mm:ss} UTC)");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }
            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log) { ConfirmationSeconds = 90 };

            var baseOff = controller.ReadCurrentOffsets();
            int mem0 = baseOff.Value.MemOffsetMHz;
            bool fired = false;
            watchdog.DriverResetDetected += () => fired = true;

            try
            {
                // Open a real safety batch so the watchdog signal must drive a
                // genuine revert - same integration a real TDR would exercise.
                var applied = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.MemoryClockOffset, () => controller.SetMemoryOffsetMhz(mem0 + 100), "baseline", "+100 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(applied, "batch opened (90s window, only a watchdog fire can revert it)");

                Thread.Sleep(3500); // let the watchdog baseline settle past the window start
                System.Diagnostics.EventLog.WriteEntry("nvlddmkm",
                    "kaliteConfig OcVerify self-test event (synthetic - not a real driver fault)",
                    System.Diagnostics.EventLogEntryType.Error, 14);
                Console.WriteLine("  injected synthetic nvlddmkm Error event");

                bool firedSoon = SpinUntil(() => fired, 8000);
                Check(firedSoon, "watchdog fired on the new event (reader + arm + tick pipeline work)");

                bool reverted = SpinUntil(() => safety.CurrentState == SafetyState.Idle &&
                                             controller.ReadCurrentOffsets().Value.MemOffsetMHz == mem0, 5000);
                Check(reverted, "watchdog fire drove the safety machine to revert to the anchor");
            }
            finally
            {
                safety.Confirm();
                SpinUntil(() => safety.CurrentState == SafetyState.Idle, 3000);
                controller.SetMemoryOffsetMhz(mem0);
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
            }
        }

        // ---------------------------------------------------------------
        // v2 Part A hardware gate (spec process step 1): the V/F curve path
        // on the REAL GPU. Run on the target RTX machine BEFORE Part B work
        // begins - Part B trusts profiles, and profiles now carry curve data.
        // Read-only first (base labels, ranges, index-alignment shape), then
        // one flat +15 MHz write through the safety machine with readback,
        // one per-point nudge, revert-to-anchor, and baseline restore.
        // Voltage-boost % is READ ONLY here (never blind-write a % slider on
        // someone else's card from a harness).
        // ---------------------------------------------------------------
        private static void VerifyVfCurve()
        {
            Console.WriteLine("--- v2 V/F curve on real hardware (read, flat write, per-point write, revert) ---");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }
            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log) { ConfirmationSeconds = 3 };

            var caps = controller.ReadCapabilities();
            if (!caps.IsSuccess) { Check(false, "capabilities"); return; }
            Console.WriteLine($"  VfCurveSupported={caps.Value.VfCurveSupported} points={caps.Value.VfCurvePointCount} " +
                              $"VoltageBoostSupported={caps.Value.VoltageBoostSupported}");
            if (!caps.Value.VfCurveSupported) { Check(false, "V/F curve exposed by this driver"); return; }

            var curve = controller.ReadVoltageFrequencyCurve();
            if (!curve.IsSuccess || curve.Value is null || curve.Value.Count == 0)
            {
                Check(false, $"ReadVoltageFrequencyCurve: {curve.ErrorKind} {curve.Detail}");
                return;
            }
            var c = curve.Value;
            var volts = c.Points.Select(p => p.VoltageMv).ToArray();
            var freqs = c.Points.Select(p => p.BaseFrequencyMHz).ToArray();
            Console.WriteLine($"  points={c.Count} volt=[{volts.First()}..{volts.Last()}] mV " +
                              $"base=[{freqs.Min()}..{freqs.Max()}] MHz " +
                              $"rangeSpan=[{c.Points.Min(p => p.MinOffsetMHz)}..{c.Points.Max(p => p.MaxOffsetMHz)}] MHz " +
                              $"vbSupported={c.VoltageBoostSupported} vbCurrent={c.CurrentVoltageBoostPercent}%");
            Console.WriteLine($"  first3: {string.Join(" ", c.Points.Take(3).Select(p => $"{p.VoltageMv}mV/{p.BaseFrequencyMHz}MHz/{p.OffsetMHz:+0;-0;0}"))}");
            Console.WriteLine($"  last3:  {string.Join(" ", c.Points.TakeLast(3).Select(p => $"{p.VoltageMv}mV/{p.BaseFrequencyMHz}MHz/{p.OffsetMHz:+0;-0;0}"))}");

            bool voltsIncreasing = volts.Zip(volts.Skip(1), (a, b) => b > a).All(x => x);
            Check(voltsIncreasing, "voltages strictly increase along the curve (1:1 index alignment with the driver table)");
            Check(freqs.Zip(freqs.Skip(1), (a, b) => b >= a).All(x => x), "base frequencies non-decreasing");
            Check(c.Points.All(p => p.OffsetMHz >= p.MinOffsetMHz && p.OffsetMHz <= p.MaxOffsetMHz),
                "live offsets sit inside their queried ranges");
            Check(c.ValidateMonotonic(c.GetOffsets(), out _), "live curve validates monotonic");

            var baseline = c.GetOffsets().ToArray();

            try
            {
                // ---- Test F1: flat +15 through the safety machine --------------
                var flat = c.ExpandFlatOffset(15);
                Check(c.ValidateMonotonic(flat, out _), "F1: flat +15 validates monotonic client-side");
                var f1 = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.VoltageFrequencyCurve,
                        () => controller.SetVoltageFrequencyCurveOffsets(flat), "baseline", "flat +15 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(f1, "F1: flat +15 batch applied");
                var readF1 = controller.ReadVfCurveOffsets();
                Check(readF1.IsSuccess && readF1.Value!.SequenceEqual(flat),
                    "F1: boost-table readback matches the written flat table");
                safety.Confirm();
                Check(safety.CurrentState == SafetyState.Idle, "F1: confirm -> Idle");

                // ---- Test F2: per-point nudge (last point +10 more) ------------
                var nudged = flat.ToArray();
                nudged[^1] = Math.Min(c.Points[^1].MaxOffsetMHz, nudged[^1] + 10);
                var f2 = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.VoltageFrequencyCurve,
                        () => controller.SetVoltageFrequencyCurveOffsets(nudged), "flat +15", "last point +10"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(f2, "F2: per-point batch applied");
                var readF2 = controller.ReadVfCurveOffsets();
                Check(readF2.IsSuccess && readF2.Value!.SequenceEqual(nudged),
                    "F2: readback matches the per-point table");

                // ---- Test F3: revert restores the F1 anchor, not zeros ---------
                safety.RevertNow();
                bool backF = SpinUntil(() => safety.CurrentState == SafetyState.Idle, 5000);
                var readF3 = controller.ReadVfCurveOffsets();
                Check(backF && readF3.IsSuccess && readF3.Value!.SequenceEqual(flat),
                    "F3: revert restored the pre-batch table (flat +15), not stock zeros");

                // ---- Test F4: count-mismatch write is refused cleanly -----------
                var bad = controller.SetVoltageFrequencyCurveOffsets(new[] { 1, 2, 3 });
                Check(!bad.IsSuccess, $"F4: 3-point write against a {c.Count}-point curve refused ({bad.Detail})");
            }
            finally
            {
                safety.Confirm();
                SpinUntil(() => safety.CurrentState == SafetyState.Idle, 3000);
                controller.SetVoltageFrequencyCurveOffsets(baseline);
                var fin = controller.ReadVfCurveOffsets();
                Check(fin.IsSuccess && fin.Value!.SequenceEqual(baseline), "final: pre-test curve offsets restored");
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
            }
        }

        private static bool SpinUntil(Func<bool> cond, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                Thread.Sleep(100);
            }
            return cond();
        }

        // ---------------------------------------------------------------
        // Step 3 (spec 9): a REAL triggered TDR. Applies the queried maximum
        // core offset (+1000 MHz on this card - far beyond silicon stability),
        // which destabilizes the driver within seconds. The safety machine is
        // set to a 90s window so ONLY the watchdog's driver-reset detection can
        // trigger the revert. If no TDR occurs within 25s the test aborts and
        // reverts manually - it never escalates past the slider-max offset.
        // ---------------------------------------------------------------
        private static void VerifyTdrWatchdog()
        {
            Console.WriteLine("--- TDR watchdog against a REAL triggered driver reset ---");
            Console.WriteLine("  Expect a screen freeze for a few seconds while Windows recovers the driver.");

            var controller = new NvApiGpuController();
            if (!controller.Initialize().IsSuccess) { Check(false, "NVAPI init"); return; }

            var watchdog = new TdrWatchdogService();
            var logger = new OverclockChangeLogger();
            var safety = new SafetyRevertService(controller, watchdog, logger.Log) { ConfirmationSeconds = 90 };

            var transitions = new System.Collections.Generic.List<string>();
            safety.StateChanged += (_, e) =>
            {
                var line = $"{e.State}" + (e.RevertReason is null ? "" : $" ({e.RevertReason})");
                if (transitions.Count == 0 || transitions[^1] != line) transitions.Add(line);
                Console.WriteLine($"  [state] {line}");
            };

            var baseOff = controller.ReadCurrentOffsets();
            if (!baseOff.IsSuccess) { Check(false, "read baseline"); return; }
            int core0 = baseOff.Value.CoreOffsetMHz;
            int mem0Base = baseOff.Value.MemOffsetMHz;
            var rangeMax = (int)(controller.ReadCapabilities().Value.CoreOffsetRangeMHz?.Maximum ?? 0);
            Console.WriteLine($"  baseline core offset {core0:+0;-0;0} MHz; queried max {rangeMax:+0;-0;0} MHz");

            try
            {
                // Probe = BOTH domains at their queried maxima: core only bites
                // under boost load, but VRAM is always active (desktop scanout),
                // so memory slider-max is the strongest idle-time destabilizer
                // available within driver-allowed ranges.
                var memMax = (int)(controller.ReadCapabilities().Value.MemOffsetRangeMHz?.Maximum ?? 0);
                var applied = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.CoreClockOffset, () => controller.SetCoreOffsetMhz(rangeMax), "baseline", $"{rangeMax:+0;-0;0} MHz (slider max)"),
                    new PendingChange(OcControlNames.MemoryClockOffset, () => controller.SetMemoryOffsetMhz(memMax), "baseline", $"{memMax:+0;-0;0} MHz (slider max)"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(applied, "TDR probe: slider-max core+mem offsets accepted by driver (destabilizing)");
                if (!applied) return;

                Console.WriteLine("  waiting up to 25s for the driver reset…");
                bool reverted = SpinUntil(() =>
                    safety.CurrentState == SafetyState.Idle &&
                    transitions.Any(t => t.StartsWith("Reverting") && t.Contains("driver reset")), 25_000);

                if (!reverted)
                {
                    // Card survived - do NOT go higher; abort cleanly.
                    Console.WriteLine("  no TDR within 25s - aborting probe (card survived slider-max; not escalating)");
                    safety.RevertNow();
                    SpinUntil(() => safety.CurrentState == SafetyState.Idle, 5000);
                    Check(false, "TDR: driver did NOT reset at slider-max offset (test inconclusive, safely reverted)");
                    return;
                }

                Check(true, "TDR: watchdog detected the real driver reset and reverted immediately");

                // Driver recovery: NVAPI may refuse calls for a moment after TDR.
                bool recovered = SpinUntil(() => controller.ReadCurrentOffsets().IsSuccess, 15_000);
                Check(recovered, "TDR: driver recovered (NVAPI answering again)");

                var after = controller.ReadCurrentOffsets();
                Check(after.IsSuccess && after.Value.CoreOffsetMHz == core0,
                    $"TDR: offset back at baseline after reset/revert (read {after.Value.CoreOffsetMHz:+0;-0;0})");

                var tdrEvent = TdrWatchdogService.TryReadLastTdrTimeUtc();
                Check(tdrEvent.HasValue && DateTime.UtcNow - tdrEvent.Value < TimeSpan.FromMinutes(2),
                    $"TDR: System event log holds the recovery event ({tdrEvent:HH:mm:ss} UTC)");

                Check(transitions.Contains("Reverting (driver reset detected)"),
                    "TDR: state machine passed through Reverting with reason 'driver reset detected'");
            }
            finally
            {
                safety.Confirm(); // close any window still open
                SpinUntil(() => safety.CurrentState == SafetyState.Idle, 5000);
                controller.SetCoreOffsetMhz(core0);
                controller.SetMemoryOffsetMhz(mem0Base);
                var final = controller.ReadCurrentOffsets();
                Check(final.IsSuccess && final.Value.CoreOffsetMHz == core0 && final.Value.MemOffsetMHz == mem0Base, "final: baseline restored");
                watchdog.Dispose();
                safety.DisposeAsync().GetAwaiter().GetResult();
            }
        }
    }
}
