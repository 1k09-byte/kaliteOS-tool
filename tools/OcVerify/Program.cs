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

            var step = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "all";
            if (step is "telemetry" or "all") VerifyTelemetry();
            if (step is "cycle" or "all") VerifySafetyCycle();
            if (step is "tdr") VerifyTdrWatchdog();
            if (step is "watchdog") VerifyWatchdogPipeline();

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
                Check(worstCoreDelta <= 400, $"core clock within 400MHz of nvidia-smi (worst delta {worstCoreDelta:0}MHz — P-state bounce)");
                Check(worstMemDelta <= 400, $"mem clock within 400MHz of nvidia-smi (worst delta {worstMemDelta:0}MHz — P-state bounce)");
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
        // the real GPU — apply/confirm, apply/expire-revert, manual revert.
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

                // The anchor is Test A's CONFIRMED +15 — last known good, not
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
                controller.SetCoreOffsetMhz(core0); // restore immediately — do not hold the clamped max offset

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
                Console.WriteLine("  " + e.Result + "  " + e.ControlName + ": " + e.OldValue + " -> " + e.NewValue + "  (" + e.Source + ")" + (e.FailureReason is null ? "" : " — " + e.FailureReason));
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
                // genuine revert — same integration a real TDR would exercise.
                var applied = safety.ApplyBatchAsync(new[]
                {
                    new PendingChange(OcControlNames.MemoryClockOffset, () => controller.SetMemoryOffsetMhz(mem0 + 100), "baseline", "+100 MHz"),
                }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
                Check(applied, "batch opened (90s window, only a watchdog fire can revert it)");

                Thread.Sleep(3500); // let the watchdog baseline settle past the window start
                System.Diagnostics.EventLog.WriteEntry("nvlddmkm",
                    "kaliteConfig OcVerify self-test event (synthetic — not a real driver fault)",
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
        // core offset (+1000 MHz on this card — far beyond silicon stability),
        // which destabilizes the driver within seconds. The safety machine is
        // set to a 90s window so ONLY the watchdog's driver-reset detection can
        // trigger the revert. If no TDR occurs within 25s the test aborts and
        // reverts manually — it never escalates past the slider-max offset.
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
                    // Card survived — do NOT go higher; abort cleanly.
                    Console.WriteLine("  no TDR within 25s — aborting probe (card survived slider-max; not escalating)");
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
