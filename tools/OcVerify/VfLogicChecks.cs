using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;

namespace OcVerify
{
    /// <summary>
    /// GPU-independent v2 logic checks: V/F curve math (monotonicity, clamping,
    /// flat expansion, base/edit separation), the shared ProfileBatchBuilder
    /// against a fake controller, the safety machine's per-control countdowns
    /// + curve/voltage revert anchors, and game-binding match rules.
    /// Runs anywhere (no NVAPI, no GPU) - the hardware half lives in
    /// Program.VerifyVfCurve, which must run on the real RTX machine.
    /// </summary>
    internal static class VfLogicChecks
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("--- v2 logic (no GPU required) ---");
            CurveMath();
            BatchBuilder();
            SafetyCurvePath();
            BindingMatches();
            AutoApply();
            GraphAxisTicks();
            FanRamp();
            HardwareQuiesce();
            Console.WriteLine(_failures == 0
                ? "  v2 logic: all passed"
                : $"  v2 logic: {_failures} FAILED");
            return _failures;
        }

        // Synthetic 8-point curve: 700..825 mV, 1500..2205 MHz stock, ±200 range.
        private static GpuVoltageFrequencyCurve SyntheticCurve()
        {
            var c = new GpuVoltageFrequencyCurve();
            for (int i = 0; i < 8; i++)
                c.Points.Add(new VfCurvePoint
                {
                    VoltageMv = 700 + i * 25,
                    BaseFrequencyMHz = 1500 + i * 100 + (i == 7 ? 5 : 0),
                    OffsetMHz = 0,
                    MinOffsetMHz = -200,
                    MaxOffsetMHz = 200,
                });
            return c;
        }

        private static void CurveMath()
        {
            var c = SyntheticCurve();

            Check(c.ValidateMonotonic(c.GetOffsets(), out var e0) && e0 is null,
                "flat stock curve is monotonic");

            var dip = new[] { 0, 0, 0, -500, 0, 0, 0, 0 };
            Check(!c.ValidateMonotonic(dip, out var e1) && e1 is not null && e1.Contains("4"),
                $"mid-curve dip rejected with a pointing error ({e1})");

            var equal = new[] { 0, 0, -100, 0, 0, 0, 0, 0 };
            // point4 base 1800-100=1700 vs point3 1700 -> equal is allowed (non-decreasing)
            Check(c.ValidateMonotonic(equal, out _), "equal adjacent frequencies allowed (non-decreasing)");

            var clamped = c.ClampOffsets(new[] { 0, 0, 0, 0, 0, 0, 0, 500 });
            Check(clamped[7] == 200 && clamped[0] == 0, "per-point clamp caps at the driver range (+500 -> +200)");

            var flat = c.ExpandFlatOffset(25);
            Check(flat.Count == 8 && flat.All(o => o == 25), "flat expansion writes the same table uniformly");

            var flatClamped = c.ExpandFlatOffset(999);
            Check(flatClamped.All(o => o == 200), "flat expansion respects per-point maxima");

            // Base/edit separation: clamping must never mutate the queried base.
            c.ClampOffsets(new[] { 10, 20, 30, 40, 50, 60, 70, 80 });
            Check(c.Points.All(p => p.OffsetMHz == 0), "ClampOffsets leaves the base curve untouched");

            Check(c.WidestOffsetRange() == (-200, 200), "widest span reported for simple-mode slider sizing");
        }

        private sealed class FakeController : INvidiaGpuController
        {
            public List<int[]> CurveWrites { get; } = new();
            public List<uint> VoltageBoostWrites { get; } = new();
            public List<string> Writes { get; } = new();
            public int[] LiveCurve = new int[8];
            public uint LiveVoltageBoost = 0;
            public bool FailCurveWrite;
            public GpuCapabilities? Caps;
            public GpuVoltageFrequencyCurve? Curve;

            public bool IsInitialized => true;
            public GpuResult Initialize() => GpuResult.Ok();
            public GpuResult<GpuIdentity> GetIdentity() => throw new NotSupportedException();
            public int TelemetryReads;
            public GpuResult<GpuTelemetrySnapshot> ReadTelemetry()
            {
                TelemetryReads++;
                return GpuResult<GpuTelemetrySnapshot>.Ok(new GpuTelemetrySnapshot { Timestamp = DateTime.Now });
            }
            public GpuResult<GpuCapabilities> ReadCapabilities()
                => Caps is not null ? GpuResult<GpuCapabilities>.Ok(Caps) : throw new NotSupportedException();
            public GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets() => GpuResult<(int, int)>.Ok((0, 0));
            public GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits() => GpuResult<(double, int?)>.Ok((100, null));
            public GpuResult SetCoreOffsetMhz(int offsetMhz) { Writes.Add($"core={offsetMhz}"); return GpuResult.Ok(); }
            public GpuResult SetMemoryOffsetMhz(int offsetMhz) { Writes.Add($"mem={offsetMhz}"); return GpuResult.Ok(); }
            public GpuResult SetPowerLimitPercent(double percent) { Writes.Add($"power={percent}"); return GpuResult.Ok(); }
            public GpuResult SetTempLimitC(int tempLimitC) => GpuResult.Ok();
            public GpuResult SetFanStaticPercent(int percent) => GpuResult.Ok();
            public GpuResult RestoreFanAuto() { Writes.Add("fan=auto"); return GpuResult.Ok(); }
            public GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve()
                => Curve is not null ? GpuResult<GpuVoltageFrequencyCurve>.Ok(Curve)
                    : GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);
            public GpuResult<int[]> ReadVfCurveOffsets() => GpuResult<int[]>.Ok((int[])LiveCurve.Clone());
            public GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz)
            {
                if (FailCurveWrite) return GpuResult.Fail(OverclockErrorKind.WriteRejected, "fake refusal");
                var arr = offsetsMhz.ToArray();
                CurveWrites.Add(arr);
                LiveCurve = (int[])arr.Clone();
                return GpuResult.Ok();
            }
            public GpuResult<uint> ReadVoltageBoostPercent() => GpuResult<uint>.Ok(LiveVoltageBoost);
            public GpuResult SetVoltageBoostPercent(uint percent)
            {
                VoltageBoostWrites.Add(percent);
                LiveVoltageBoost = percent;
                return GpuResult.Ok();
            }
        }

        private sealed class FakeWatchdog : ITdrWatchdog
        {
            public event Action? DriverResetDetected;
            public void Arm() { }
            public void Disarm() { }
            public void Dispose() { }
            public void Fire() => DriverResetDetected?.Invoke();
        }

        private static GpuCapabilities TestCaps() => new()
        {
            GpuName = "fake",
            DriverVersion = "0",
            CoreOffsetRangeMHz = new OverclockControlRange(-200, 200, 1),
            MemOffsetRangeMHz = new OverclockControlRange(-200, 200, 1),
            PowerLimitRangePercent = new OverclockControlRange(50, 120, 1),
            TempLimitRangeC = null,
            VfCurveSupported = true,
        };

        private static void BatchBuilder()
        {
            var fake = new FakeController();
            var caps = TestCaps();
            var live = SyntheticCurve();

            var profile = new OverclockProfile
            {
                Name = "p",
                CoreOffsetMHz = 50,
                VfCurveOffsets = new List<int> { 10, 10, 10, 10, 10, 10, 10, 10 },
                GlobalVoltageBoostOffsetMHz = 99, // table must win over flat
            };
            var batch = ProfileBatchBuilder.Build(fake, caps, profile, _ => "old", live, out var skip);
            Check(skip is null, "matching table builds with no skip");
            Check(batch.Count == 2 && batch.Any(b => b.ControlName == OcControlNames.VoltageFrequencyCurve),
                "batch holds core + V/F curve changes");
            foreach (var ch in batch) ch.Apply();
            Check(fake.CurveWrites.Count == 1 && fake.CurveWrites[0].All(o => o == 10),
                "table wins over the flat value when both are stored");

            var flatOnly = new OverclockProfile { Name = "f", GlobalVoltageBoostOffsetMHz = 25 };
            var batchF = ProfileBatchBuilder.Build(fake, caps, flatOnly, _ => "old", live, out var skipF);
            Check(skipF is null && batchF.Count == 1, "flat-only profile expands to one curve change");
            batchF[0].Apply();
            Check(fake.CurveWrites[^1].All(o => o == 25), "flat expands uniformly across all points");

            var mismatched = new OverclockProfile { Name = "m", CoreOffsetMHz = 10, VfCurveOffsets = new List<int> { 1, 2, 3 } };
            var batchM = ProfileBatchBuilder.Build(fake, caps, mismatched, _ => "old", live, out var skipM);
            Check(skipM is not null && skipM.Contains("3") && batchM.Count == 1
                  && batchM[0].ControlName == OcControlNames.CoreClockOffset,
                $"count mismatch skips ONLY the curve ({skipM})");

            var nonMono = new OverclockProfile
            {
                Name = "n",
                VfCurveOffsets = new List<int> { 0, 0, 0, -500, 0, 0, 0, 0 },
            };
            var batchN = ProfileBatchBuilder.Build(fake, caps, nonMono, _ => "old", live, out var skipN);
            Check(batchN.Count == 0 && skipN is not null && skipN.Contains("skipped"),
                "non-monotonic saved table never reaches the driver");

            var noCurve = new OverclockProfile { Name = "c", CoreOffsetMHz = 5 };
            var batchC = ProfileBatchBuilder.Build(fake, caps, noCurve, _ => "old", live, out var skipC);
            Check(skipC is null && batchC.Count == 1, "profile without curve settings leaves the curve alone");
        }

        private static void SafetyCurvePath()
        {
            var fake = new FakeController { LiveCurve = new[] { 5, 5, 5, 5, 5, 5, 5, 5 } };
            var logger = new OverclockChangeLogger(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ocverify-logic-" + Guid.NewGuid().ToString("N")));
            var watchdog = new FakeWatchdog();
            var safety = new SafetyRevertService(fake, watchdog, logger.Log);

            int seenWindow = -1;
            safety.StateChanged += (_, e) =>
            {
                if (e.State == SafetyState.AwaitingConfirmation) seenWindow = e.CountdownSecondsRemaining;
            };

            // Curve batch -> longer window (22 default, not the 15 global).
            var curveBatch = new[]
            {
                new PendingChange(OcControlNames.VoltageFrequencyCurve,
                    () => fake.SetVoltageFrequencyCurveOffsets(new[] { 30, 30, 30, 30, 30, 30, 30, 30 }),
                    "old", "new"),
            };
            Check(safety.ApplyBatchAsync(curveBatch, OverclockChangeSource.Manual).GetAwaiter().GetResult(),
                "curve batch applies");
            Check(seenWindow == 22, $"curve batch opens a 22s window (saw {seenWindow}s)");
            Check(fake.LiveCurve.All(o => o == 30), "curve write landed on the fake");

            // Timeout revert must restore the PRE-batch per-point anchor.
            safety.RevertNow();
            Check(fake.LiveCurve.All(o => o == 5), "curve revert restores pre-batch per-point offsets (not zeros)");
            Check(safety.CurrentState == SafetyState.Idle, "machine idle after curve revert");

            // Plain batch still uses the global default.
            seenWindow = -1;
            var plainBatch = new[]
            {
                new PendingChange(OcControlNames.CoreClockOffset, () => fake.SetCoreOffsetMhz(10), "old", "new"),
            };
            safety.ApplyBatchAsync(plainBatch, OverclockChangeSource.Manual).GetAwaiter().GetResult();
            Check(seenWindow == 15, $"plain batch keeps the 15s window (saw {seenWindow}s)");
            safety.Confirm();

            // Voltage-boost revert restores its anchor too.
            fake.LiveVoltageBoost = 10;
            var vbBatch = new[]
            {
                new PendingChange(OcControlNames.VoltageBoost, () => fake.SetVoltageBoostPercent(60), "old", "new"),
            };
            safety.ApplyBatchAsync(vbBatch, OverclockChangeSource.Manual).GetAwaiter().GetResult();
            safety.RevertNow();
            Check(fake.LiveVoltageBoost == 10, "voltage-boost revert restores the pre-batch percent");

            // Failed curve write never opens a window.
            fake.FailCurveWrite = true;
            seenWindow = -1;
            var failBatch = new[]
            {
                new PendingChange(OcControlNames.VoltageFrequencyCurve,
                    () => fake.SetVoltageFrequencyCurveOffsets(new[] { 1, 1, 1, 1, 1, 1, 1, 1 }),
                    "old", "new"),
            };
            Check(!safety.ApplyBatchAsync(failBatch, OverclockChangeSource.Manual).GetAwaiter().GetResult()
                  && seenWindow == -1 && safety.CurrentState == SafetyState.Idle,
                "refused curve write opens no confirmation window");

            safety.DisposeAsync().GetAwaiter().GetResult();
        }

        private static void BindingMatches()
        {
            var b = new GameProfileBinding { ExecutableName = "Valorant.exe", ProfileId = Guid.NewGuid() };
            Check(b.Matches("VALORANT.EXE", null), "match is case-insensitive");
            Check(b.Matches("Valorant", null), ".exe suffix tolerated on the process side");
            Check(!b.Matches("NotValorant.exe", null), "different exe does not match");

            var full = new GameProfileBinding
            {
                ExecutableName = "Game.exe",
                FullPath = @"C:\Games\A\Game.exe",
                ProfileId = Guid.NewGuid(),
            };
            Check(full.Matches("Game.exe", @"C:\Games\A\Game.exe"), "full-path override matches same path");
            Check(!full.Matches("Game.exe", @"C:\Games\B\Game.exe"), "full-path override disambiguates same-named exes");
            Check(!full.Matches("Game.exe", null), "path override requires a known process path");
            Check(new GameProfileBinding { ExecutableName = "", ProfileId = Guid.NewGuid() }.Matches("x.exe", null) == false,
                "empty binding never matches");
        }

        // Unattended auto-switch decision paths, driven directly (no real
        // processes): validation gate, unknown exe, busy-machine skip,
        // exit-to-default, multi-game survivor, deleted-profile binding,
        // and TDR-during-window fallback. Timing is shrunk via 1s windows.
        private static void AutoApply()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "ocverify-auto-" + Guid.NewGuid().ToString("N"));
            var profiles = new ProfileStorageService(dir);
            var log = new OverclockChangeLogger(System.IO.Path.Combine(dir, "logs"));
            var fake = new FakeController
            {
                Caps = new GpuCapabilities
                {
                    GpuName = "fake",
                    DriverVersion = "0",
                    CoreOffsetRangeMHz = new OverclockControlRange(-200, 200, 1),
                    MemOffsetRangeMHz = new OverclockControlRange(-200, 200, 1),
                    PowerLimitRangePercent = new OverclockControlRange(50, 120, 1),
                },
            };
            var watchdog = new FakeWatchdog();
            var safety = new SafetyRevertService(fake, watchdog, log.Log) { ConfirmationSeconds = 1 };
            var t0 = DateTime.Now;
            string Stamp() => $"+{(DateTime.Now - t0).TotalSeconds:0.0}s";
            safety.StateChanged += (_, e) =>
                Console.WriteLine($"  [safety {Stamp()}] {e.State}({e.CountdownSecondsRemaining}) {e.RevertReason ?? ""}");
            var svc = new GameProfileAutoApplyService(
                fake, safety, profiles, log, fanCurve: null, watcher: new GameProcessWatcherService(),
                trace: msg => Console.WriteLine($"  [trace {Stamp()}] " + msg))
            {
                AutoApplyConfirmationSeconds = 1,
                UnattendedWaitSlackSeconds = 1,
            };

            var validated = new OverclockProfile
            {
                Name = "Ranked", CoreOffsetMHz = 20,
                HasBeenManuallyValidated = true, LastValidatedAt = DateTime.Now,
            };
            var validatedB = new OverclockProfile
            {
                Name = "Chill", CoreOffsetMHz = 30,
                HasBeenManuallyValidated = true, LastValidatedAt = DateTime.Now,
            };
            var unvalidated = new OverclockProfile { Name = "Fresh", CoreOffsetMHz = 40 };
            var def = new OverclockProfile
            {
                Name = "Daily", CoreOffsetMHz = 5,
                HasBeenManuallyValidated = true, LastValidatedAt = DateTime.Now,
                ConfirmedAt = DateTime.Now,
            };
            profiles.Save(validated);
            profiles.Save(validatedB);
            profiles.Save(unvalidated);
            profiles.Save(def);
            profiles.SetDefaultProfile("Daily");
            profiles.SaveBinding(new GameProfileBinding { ExecutableName = "GameA.exe", ProfileId = validated.Id });
            profiles.SaveBinding(new GameProfileBinding { ExecutableName = "GameB.exe", ProfileId = validatedB.Id });
            profiles.SaveBinding(new GameProfileBinding { ExecutableName = "Fresh.exe", ProfileId = unvalidated.Id });
            profiles.SaveBinding(new GameProfileBinding { ExecutableName = "Ghost.exe", ProfileId = Guid.NewGuid() });

            static GameProcessWatcherService.ProcessEventInfo Ev(string name, int pid)
                => new(name, pid, null);

            bool WaitFor(Func<bool> cond, int timeoutMs = 15000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (cond()) return true;
                    System.Threading.Thread.Sleep(100);
                }
                return cond();
            }

            // Applies run on background tasks: never start the next scenario
            // while one is still settling, or exits/starts interleave.
            void Settle(int timeoutMs = 25000)
            {
                WaitFor(() => !svc.IsApplyInFlight && safety.CurrentState == SafetyState.Idle, timeoutMs);
            }

            // 1: unvalidated profile never auto-applies.
            svc.HandleProcessStarted(Ev("Fresh.exe", 101));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").Contains("not yet validated"))
                  && !fake.Writes.Any(w => w == "core=40"),
                "unvalidated profile is ignored (no write, validation hint published)");
            svc.HandleProcessStopped(Ev("Fresh.exe", 101));

            // 2: unknown exe is ignored silently (no binding -> no status churn).
            int writesBefore = fake.Writes.Count;
            svc.HandleProcessStarted(Ev("Notepad.exe", 102));
            System.Threading.Thread.Sleep(500);
            Check(fake.Writes.Count == writesBefore, "unbound executable triggers nothing");
            svc.HandleProcessStopped(Ev("Notepad.exe", 102));

            // 3: validated start applies through the silent window.
            svc.HandleProcessStarted(Ev("GameA.exe", 1111));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").StartsWith("Applied profile 'Ranked'"))
                  && fake.Writes.Contains("core=20"),
                "validated start auto-applies ('Ranked', core=20)");
            Check(svc.Current.ActiveProfileName == "Ranked", "active profile tracked");
            Settle();

            // 4: busy safety machine -> skip, never stomp.
            var manual = safety.ApplyBatchAsync(new[]
            {
                new PendingChange(OcControlNames.CoreClockOffset, () => fake.SetCoreOffsetMhz(1), "o", "n"),
            }, OverclockChangeSource.Manual).GetAwaiter().GetResult();
            Check(manual, "manual window opened for the busy test");
            svc.HandleProcessStarted(Ev("GameB.exe", 2222));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").Contains("confirmation window")),
                "auto-apply skipped while a manual confirmation is in flight");
            safety.Confirm();
            WaitFor(() => safety.CurrentState == SafetyState.Idle);
            svc.HandleProcessStopped(Ev("GameB.exe", 2222));
            Settle(); // skipped start restored the anchor; the stop is a no-op

            // 5: exit with nobody else running -> default returns.
            svc.HandleProcessStopped(Ev("GameA.exe", 1111));
            // Writes land at batch-apply time; the status message publishes
            // after the silent window closes - poll for BOTH, not one-then-other.
            bool t5 = WaitFor(() => fake.Writes.Contains("core=5")
                      && (svc.Current.LastMessage ?? "").Contains("Daily"));
            if (!t5)
                Console.WriteLine($"  [debug t5] last='{svc.Current.LastMessage}' safety={safety.CurrentState}");
            Check(t5, "game exit reverts to the designated default ('Daily', core=5)");
            Settle();

            // 6: multi-game - most-recent wins; survivor keeps its profile.
            int mark = fake.Writes.Count;
            svc.HandleProcessStarted(Ev("GameA.exe", 1111));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").StartsWith("Applied profile 'Ranked'")), "A started again");
            Settle();
            svc.HandleProcessStarted(Ev("GameB.exe", 2222));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").StartsWith("Applied profile 'Chill'"))
                  && fake.Writes.Contains("core=30"), "most-recent launch wins (B active)");
            Settle();
            int mark2 = fake.Writes.Count;
            svc.HandleProcessStopped(Ev("GameB.exe", 2222));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").Contains("keeping"))
                  && fake.Writes.Count == mark2, "exit with a bound survivor keeps the profile (no revert, no write)");
            Settle();
            svc.HandleProcessStopped(Ev("GameA.exe", 1111));
            Check(WaitFor(() => fake.Writes.Count > mark2 && fake.Writes.Contains("core=5")),
                "last bound game exiting falls back to default");
            Settle();

            // 7: binding to a deleted profile is skipped with a hint.
            svc.HandleProcessStarted(Ev("Ghost.exe", 103));
            Check(WaitFor(() => (svc.Current.LastMessage ?? "").Contains("deleted")),
                "deleted-profile binding surfaces a hint instead of applying");
            svc.HandleProcessStopped(Ev("Ghost.exe", 103));
            Settle();

            // 8: TDR during the silent window -> revert + default + report.
            svc.HandleProcessStarted(Ev("GameA.exe", 1111));
            bool sawWindow = WaitFor(() => safety.CurrentState == SafetyState.AwaitingConfirmation, 8000);
            if (sawWindow) watchdog.Fire();
            Check(sawWindow
                  && WaitFor(() => (svc.Current.LastMessage ?? "").Contains("Driver reset"))
                  && fake.Writes.Contains("core=5"),
                "driver reset after auto-apply reverts and applies default, with a visible report");
            svc.HandleProcessStopped(Ev("GameA.exe", 1111));

            // 9: watcher lifecycle is safe without bindings resolving.
            var watcher = new GameProcessWatcherService();
            watcher.SetWatchedExecutables(new[] { "GameA.exe" });
            watcher.Start();
            Check(watcher.IsRunning, $"watcher runs ({watcher.ModeDescription})");
            watcher.Stop();
            Check(!watcher.IsRunning, "watcher stops cleanly");
            watcher.Dispose();

            svc.Dispose();
            safety.DisposeAsync().GetAwaiter().GetResult();
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
        // Graph axis ticks: the exact helpers the canvas uses.
        private static void GraphAxisTicks()
        {
            var (xStep, xFirst) = GraphTicks.NiceTicks(700, 1075, 6);
            Check(xStep == 100 && xFirst == 700, $"volt ticks step 100 from 700 (got {xStep}/{xFirst})");

            var (yStep, yFirst) = GraphTicks.NiceTicks(1500, 2205, 5);
            Check(yStep == 200 && yFirst == 1600, $"freq ticks step 200 from 1600 (got {yStep}/{yFirst})");

            var (sStep, sFirst) = GraphTicks.NiceTicks(800, 825, 6);
            Check(sFirst >= 800 && sStep <= 10, $"narrow span still ticks inside range (step {sStep}, first {sFirst})");

            Check(GraphTicks.Label(750) == "750" && GraphTicks.Label(2.5) == "2.5",
                "tick labels compact (int plain, fraction one decimal)");

            // Full tick sweep stays inside the data range and covers it.
            var ticks = new System.Collections.Generic.List<double>();
            for (double t = xFirst; t <= 1075 + 1e-9; t += xStep) ticks.Add(t);
            Check(ticks.Count >= 3 && ticks.Count <= 7 && ticks[0] >= 700 && ticks[^1] <= 1075,
                $"volt sweep yields {ticks.Count} in-range ticks ({string.Join(",", ticks)})");
        }
        // Fan ramp/smoothing step used by the curve loop.
        private static void FanRamp()
        {
            Check(FanCurveExecutionService.RampTowards(30, 80, 0) == 80,
                "ramp 0 disables smoothing (target directly)");
            Check(FanCurveExecutionService.RampTowards(30, 80, 10) == 40,
                "ramp climbs by at most maxStep");
            Check(FanCurveExecutionService.RampTowards(80, 30, 10) == 70,
                "ramp descends by at most maxStep");
            Check(FanCurveExecutionService.RampTowards(75, 80, 10) == 80,
                "ramp snaps when closer than maxStep");
            Check(FanCurveExecutionService.RampTowards(50, 50, 5) == 50,
                "ramp holds a reached target");
        }
        // Hardware quiesce gate: the 0xc0000005 guard for device restarts.
        private static void HardwareQuiesce()
        {
            Check(!HardwareQuiesceGate.IsQuiesced, "gate starts open");
            using (HardwareQuiesceGate.Hold("test"))
            {
                Check(HardwareQuiesceGate.IsQuiesced, "hold quiesces");
                Check(HardwareQuiesceGate.Reason == "test", "hold reason visible");
                using (HardwareQuiesceGate.Hold("inner"))
                    Check(HardwareQuiesceGate.IsQuiesced, "nested hold stays quiesced");
                Check(HardwareQuiesceGate.IsQuiesced, "inner release keeps outer hold");
            }
            Check(!HardwareQuiesceGate.IsQuiesced && HardwareQuiesceGate.Reason is null,
                "release reopens gate and clears reason");

            // Polling loop performs zero native reads while held, resumes after.
            var fake = new FakeController();
            var polling = new GpuTelemetryPollingService(fake, TimeSpan.FromMilliseconds(100));
            int updates = 0;
            polling.Update += _ => System.Threading.Interlocked.Increment(ref updates);
            polling.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (updates < 2 && sw.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(50);
            Check(updates >= 2, "telemetry flows before hold");
            int readsBefore = fake.TelemetryReads;
            using (HardwareQuiesceGate.Hold("test"))
            {
                System.Threading.Thread.Sleep(500);
                Check(fake.TelemetryReads == readsBefore, "no native reads while quiesced");
            }
            sw.Restart();
            while (fake.TelemetryReads <= readsBefore && sw.ElapsedMilliseconds < 3000)
                System.Threading.Thread.Sleep(50);
            Check(fake.TelemetryReads > readsBefore, "native reads resume after release");
            polling.Dispose();
        }
    }
}
