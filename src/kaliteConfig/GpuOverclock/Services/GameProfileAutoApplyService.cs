// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;
using static kaliteConfig.GpuOverclock.Services.GameProcessWatcherService;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Per-game profile auto-switch (v2 Part B).
    ///
    /// THE SAFETY TENSION (resolved explicitly): the safety state machine's
    /// AwaitingConfirmation countdown assumes a user is present to confirm,
    /// but game-launch switches fire unattended - the user may be in a
    /// full-screen game. Blocking on an unconfirmable countdown is unusable;
    /// silently skipping the safety layer is the anti-pattern this module
    /// was built to avoid. Resolution:
    ///
    /// - Only profiles with HasBeenManuallyValidated (manually applied AND
    ///   confirmed through the normal interactive machine in a desktop
    ///   context, at least once) are eligible. Startup reapply and previous
    ///   auto-applies never set that flag.
    /// - Eligible auto-applies reuse the headless pattern proven by startup
    ///   reapply: the SAME safety machine, TDR watchdog armed the whole
    ///   time, window marked silent (ConfirmSilently) so it expires into
    ///   confirm instead of blocking on invisible UI. The in-app banner still
    ///   shows when the app IS visible, so switches are never invisible.
    /// - A driver reset shortly after auto-apply immediately reverts (the
    ///   machine does it) and we then apply the default profile + publish a
    ///   status the user sees after the game closes (plus log entries).
    /// - A refused write falls back to the default profile immediately -
    ///   never a partially-applied, unconfirmed state while in-game.
    /// - If the machine is busy (user mid-confirmation), the auto-apply is
    ///   SKIPPED, not queued - never stomp an interactive session.
    ///
    /// Game-exit: the default profile returns only when no OTHER bound game
    /// is still running (multi-game edge case). When no default is
    /// designated (or it isn't trustworthy), a synthetic stock profile
    /// (core/mem 0, power 100%, zeroed curve) is applied instead.
    /// </summary>
    public sealed class GameProfileAutoApplyService : IDisposable
    {
        public sealed record AutoApplyStatus(
            string? ActiveProfileName,
            string? ActiveExecutable,
            string? LastMessage,
            DateTime LastMessageAt,
            string WatcherMode);

        public event Action<AutoApplyStatus>? StatusChanged;

        /// <summary>Silent-window length for game applies (curve batches still use their longer override).</summary>
        public int AutoApplyConfirmationSeconds { get; set; } = 10;

        /// <summary>Extra slack past the window end before giving up the wait (tests shrink this).</summary>
        public int UnattendedWaitSlackSeconds { get; set; } = 10;

        private readonly INvidiaGpuController _controller;
        private readonly SafetyRevertService _safety;
        private readonly ProfileStorageService _profiles;
        private readonly OverclockChangeLogger _log;
        private readonly FanCurveExecutionService? _fanCurve;
        private readonly GameProcessWatcherService _watcher;
        private readonly bool _ownsWatcher;
        private readonly Action<string>? _trace;
        private readonly object _gate = new();

        private bool _started;
        private bool _disposed;
        private bool _applyInFlight;
        private bool _inFallback;

        private readonly Dictionary<int, string> _runningPids = new(); // pid -> exe name
        private GameProfileBinding? _activeBinding;
        private OverclockProfile? _activeProfile;
        private int _activePid;

        private AutoApplyStatus _current =
            new(null, null, "Auto-switch idle.", DateTime.Now, "watcher stopped");

        public AutoApplyStatus Current { get { lock (_gate) return _current; } }

        public GameProfileAutoApplyService(
            INvidiaGpuController controller,
            SafetyRevertService safety,
            ProfileStorageService profiles,
            OverclockChangeLogger log,
            FanCurveExecutionService? fanCurve = null,
            GameProcessWatcherService? watcher = null,
            Action<string>? trace = null)
        {
            _controller = controller;
            _safety = safety;
            _profiles = profiles;
            _log = log;
            _fanCurve = fanCurve;
            _trace = trace;
            _watcher = watcher ?? new GameProcessWatcherService();
            _ownsWatcher = watcher is null;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _started) return;
                _started = true;
            }
            RefreshBindings();
            _watcher.ProcessStarted += HandleProcessStarted;
            _watcher.ProcessStopped += HandleProcessStopped;
            _watcher.Start();
            Publish(null, null, $"Auto-switch watching ({_watcher.ModeDescription}).");
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_started) return;
                _started = false;
            }
            _watcher.ProcessStarted -= HandleProcessStarted;
            _watcher.ProcessStopped -= HandleProcessStopped;
            _watcher.Stop();
        }

        /// <summary>Re-reads bindings from disk (call after bind/unbind/toggle).</summary>
        public void RefreshBindings()
        {
            var names = _profiles.LoadBindings()
                .Where(b => b.Enabled)
                .Select(b => b.NormalizedExecutableName)
                .Where(n => n.Length > 0);
            _watcher.SetWatchedExecutables(names);
        }

        // Public so the OcVerify harness (and future UI tests) can drive the
        // decision logic deterministically without spawning processes.
        public void HandleProcessStarted(ProcessEventInfo info) => _ = Task.Run(() => OnStarted(info));

        // Public for the same reason as HandleProcessStarted.
        public void HandleProcessStopped(ProcessEventInfo info) => _ = Task.Run(() => OnStopped(info));

        private void OnStarted(ProcessEventInfo info)
        {
            lock (_gate) _runningPids[info.ProcessId] = info.ProcessName;

            var binding = FindBestBinding(info);
            if (binding is null) return; // not a bound executable

            var profile = _profiles.FindProfile(binding.ProfileId);
            if (profile is null)
            {
                Log("Game auto-switch", binding.ExecutableName, profile?.Name ?? "?",
                    OverclockChangeResult.Failed, "binding points to a deleted profile");
                Publish(_activeProfile?.Name, _activeBinding?.ExecutableName,
                    $"Ignored {info.ProcessName}: its profile was deleted. Re-bind or remove the binding.");
                return;
            }

            if (!profile.HasBeenManuallyValidated)
            {
                _log.Log(new AppliedChangeLogEntry
                {
                    Timestamp = DateTime.Now,
                    ControlName = "Game auto-switch",
                    OldValue = info.ProcessName,
                    NewValue = profile.Name,
                    Source = OverclockChangeSource.GameAutoApply,
                    Result = OverclockChangeResult.Failed,
                    FailureReason = "profile is not manually validated - apply it manually once to enable auto-switch",
                });
                Publish(_activeProfile?.Name, _activeBinding?.ExecutableName,
                    $"Ignored {info.ProcessName}: '{profile.Name}' is not yet validated - apply it manually once to enable auto-switch.");
                return;
            }

            // Optimistic anchor (so an exit mid-window still resolves), with
            // restore on the provably-untouched skip paths below.
            GameProfileBinding? prevBinding;
            OverclockProfile? prevProfile;
            int prevPid;
            lock (_gate)
            {
                prevBinding = _activeBinding;
                prevProfile = _activeProfile;
                prevPid = _activePid;
                _activeBinding = binding;
                _activeProfile = profile;
                _activePid = info.ProcessId;
            }

            var outcome = ApplyUnattended(profile, $"for {info.ProcessName}");
            if (outcome == ApplyOutcome.Applied)
            {
                // Most-recent launch wins when several bound games run at once.
                Publish(profile.Name, info.ProcessName,
                    $"Applied profile '{profile.Name}' for {info.ProcessName}.");
            }
            else if (outcome == ApplyOutcome.SkippedInFlight)
            {
                // Our own earlier apply is still settling - retry once it
                // lands (bounded; background thread). The GPU was untouched.
                lock (_gate)
                {
                    _activeBinding = prevBinding;
                    _activeProfile = prevProfile;
                    _activePid = prevPid;
                }
                if (WaitForApplySettle(TimeSpan.FromSeconds(60)))
                {
                    lock (_gate)
                    {
                        _activeBinding = binding;
                        _activeProfile = profile;
                        _activePid = info.ProcessId;
                    }
                    if (ApplyUnattended(profile, $"for {info.ProcessName} (retry)") == ApplyOutcome.Applied)
                        Publish(profile.Name, info.ProcessName,
                            $"Applied profile '{profile.Name}' for {info.ProcessName}.");
                }
                else
                {
                    Publish(prevProfile?.Name, prevBinding?.ExecutableName,
                        $"Skipped auto-switch for {info.ProcessName}: another switch was still settling.");
                }
            }
            else if (outcome == ApplyOutcome.SkippedManualBusy)
            {
                // A user confirmation window is open - GPU provably untouched.
                lock (_gate)
                {
                    _activeBinding = prevBinding;
                    _activeProfile = prevProfile;
                    _activePid = prevPid;
                }
            }
            // ApplyOutcome.Failed: the fallback already ran and re-anchored.
        }

        private void OnStopped(ProcessEventInfo info)
        {
            lock (_gate) _runningPids.Remove(info.ProcessId);

            // An apply for the exiting game may still be settling (launched
            // seconds ago): decide on the RESOLVED state, not the mid-window
            // one. Bounded wait on a background thread - never the UI thread.
            WaitForApplySettle(TimeSpan.FromSeconds(45));

            GameProfileBinding? active;
            OverclockProfile? activeProfile;
            int activePid;
            lock (_gate)
            {
                active = _activeBinding;
                activeProfile = _activeProfile;
                activePid = _activePid;
            }
            if (active is null || info.ProcessId != activePid) return; // not the game we switched for

            // Multi-game edge case: another bound game still running → leave
            // the currently-applicable profile active, but re-anchor to the
            // survivor so ITS exit triggers the revert.
            var survivor = FindRemainingBoundPid();
            if (survivor is not null)
            {
                lock (_gate) _activePid = survivor.Value.pid;
                Publish(activeProfile?.Name, survivor.Value.name,
                    $"'{info.ProcessName}' exited - keeping '{activeProfile?.Name}' active for {survivor.Value.name}.");
                return;
            }

            lock (_gate)
            {
                _activeBinding = null;
                _activeProfile = null;
                _activePid = 0;
            }
            ApplyDefaultOrStock($"'{info.ProcessName}' exited");
        }

        /// <summary>Best enabled binding for a started process: exact full-path match wins, else a generic name binding.</summary>
        private GameProfileBinding? FindBestBinding(ProcessEventInfo info)
        {
            var want = GameProcessWatcherService.Normalize(info.ProcessName);
            if (want.Length == 0) return null;
            var enabled = _profiles.LoadBindings()
                .Where(b => b.Enabled && b.NormalizedExecutableName.Equals(want, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (enabled.Count == 0) return null;

            var pathSpecific = enabled.Where(b => !string.IsNullOrWhiteSpace(b.FullPath)).ToList();
            if (pathSpecific.Count > 0)
            {
                var path = info.FullPath ?? GameProcessWatcherService.TryGetProcessPath(info.ProcessId);
                foreach (var b in pathSpecific)
                    if (b.Matches(info.ProcessName, path)) return b;
            }
            return enabled.FirstOrDefault(b => string.IsNullOrWhiteSpace(b.FullPath));
        }

        private (int pid, string name)? FindRemainingBoundPid()
        {
            Dictionary<int, string> running;
            lock (_gate) running = new Dictionary<int, string>(_runningPids);
            var enabled = _profiles.LoadBindings().Where(b => b.Enabled).ToList();
            foreach (var (pid, name) in running)
            {
                var norm = GameProcessWatcherService.Normalize(name);
                if (enabled.Any(b => b.NormalizedExecutableName.Equals(norm, StringComparison.OrdinalIgnoreCase)))
                    return (pid, name);
            }
            return null;
        }

        /// <summary>Outcome of one unattended apply (drives anchor restore/retry in OnStarted).</summary>
        private enum ApplyOutcome
        {
            Applied,
            /// <summary>A user confirmation window is open - GPU provably untouched.</summary>
            SkippedManualBusy,
            /// <summary>Our own earlier apply still settling - GPU untouched by THIS call.</summary>
            SkippedInFlight,
            /// <summary>Write refused or TDR-reverted - the default/stock fallback already ran.</summary>
            Failed,
        }

        /// <summary>True while an unattended apply holds the machine (tests poll this to sequence scenarios).</summary>
        public bool IsApplyInFlight { get { lock (_gate) return _applyInFlight; } }

        /// <summary>Bounded wait until no apply is in flight and the machine is idle.</summary>
        public bool WaitForApplySettle(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                bool busy;
                lock (_gate) busy = _applyInFlight;
                if (!busy && _safety.CurrentState == SafetyState.Idle) return true;
                Thread.Sleep(100);
            }
            bool stillBusy;
            lock (_gate) stillBusy = _applyInFlight;
            return !stillBusy && _safety.CurrentState == SafetyState.Idle;
        }

        /// <summary>
        /// Applies a profile through the safety machine with a silent window
        /// (watchdog armed throughout). Returns true when the window closed
        /// cleanly; on refusal or TDR-revert falls back to default/stock.
        /// </summary>
        private ApplyOutcome ApplyUnattended(OverclockProfile profile, string reason)
        {
            lock (_gate)
            {
                if (_applyInFlight)
                {
                    _trace?.Invoke($"auto-apply skipped {reason}: another apply in flight");
                    return ApplyOutcome.SkippedInFlight;
                }
                _applyInFlight = true;
            }
            try
            {
                if (_safety.CurrentState != SafetyState.Idle)
                {
                    _log.Log(new AppliedChangeLogEntry
                    {
                        Timestamp = DateTime.Now,
                        ControlName = "Game auto-switch",
                        OldValue = reason,
                        NewValue = profile.Name,
                        Source = OverclockChangeSource.GameAutoApply,
                        Result = OverclockChangeResult.Failed,
                        FailureReason = "skipped - a manual confirmation window is in flight; leaving the GPU alone",
                    });
                    Publish(_activeProfile?.Name, _activeBinding?.ExecutableName,
                        $"Skipped auto-switch {reason}: you have a confirmation window open.");
                    return ApplyOutcome.SkippedManualBusy;
                }

                var caps = _controller.ReadCapabilities();
                var c = caps.IsSuccess ? caps.Value : null;
                if (c is null)
                {
                    _trace?.Invoke("auto-apply: capabilities unavailable");
                    FallbackToDefault("GPU capabilities unavailable");
                    return ApplyOutcome.Failed;
                }

                GpuVoltageFrequencyCurve? liveCurve = null;
                if (c.VfCurveSupported)
                {
                    var cv = _controller.ReadVoltageFrequencyCurve();
                    if (cv.IsSuccess) liveCurve = cv.Value;
                }

                var batch = ProfileBatchBuilder.Build(
                    _controller, c, profile, _ => "auto-switch previous",
                    liveCurve, out string? curveSkipped);
                if (curveSkipped is not null) _trace?.Invoke($"auto-apply: {curveSkipped}");

                bool reverted = false;
                void OnState(object? s, SafetyStateChangedEventArgs e)
                {
                    if (e.State == SafetyState.Reverting) { lock (_gate) reverted = true; }
                }

                var previousWindow = _safety.ConfirmationSeconds;
                _safety.ConfirmationSeconds = AutoApplyConfirmationSeconds;
                try
                {
                    _safety.StateChanged += OnState;
                    try
                    {
                        if (batch.Count > 0)
                        {
                            var applied = _safety.ApplyBatchAsync(batch, OverclockChangeSource.GameAutoApply)
                                .GetAwaiter().GetResult();
                            if (!applied)
                            {
                                _trace?.Invoke("auto-apply: the driver refused one or more profile values");
                                FallbackToDefault("the driver refused the profile values");
                                return ApplyOutcome.Failed;
                            }
                            _safety.ConfirmSilently(); // unattended: window expires, watchdog armed
                            int window = _safety.GetConfirmationSecondsFor(batch.Select(b => b.ControlName));
                            WaitForSafetyIdle(TimeSpan.FromSeconds(window + UnattendedWaitSlackSeconds));
                        }

                        bool wasReverted;
                        lock (_gate) wasReverted = reverted;
                        if (wasReverted)
                        {
                            _log.Log(new AppliedChangeLogEntry
                            {
                                Timestamp = DateTime.Now,
                                ControlName = "Game auto-switch",
                                OldValue = profile.Name,
                                NewValue = "default",
                                Source = OverclockChangeSource.GameAutoApply,
                                Result = OverclockChangeResult.Reverted,
                                FailureReason = "driver reset shortly after auto-apply - reverted, applying default",
                            });
                            Publish(null, null,
                                $"Driver reset after applying '{profile.Name}' - reverted and applied the default profile.");
                            FallbackToDefault("driver reset after auto-apply");
                            return ApplyOutcome.Failed;
                        }
                    }
                    finally
                    {
                        _safety.StateChanged -= OnState;
                    }
                }
                finally
                {
                    _safety.ConfirmationSeconds = previousWindow;
                }

                ApplyProfileFan(profile);

                profile.LastAppliedAt = DateTime.Now;
                _profiles.Save(profile); // timestamps only - validation flags untouched (never set here)
                return ApplyOutcome.Applied;
            }
            finally
            {
                lock (_gate) _applyInFlight = false;
            }
        }

        /// <summary>Fan half of a profile in the unattended path (mirrors the manual fan semantics).</summary>
        private void ApplyProfileFan(OverclockProfile profile)
        {
            try
            {
                if (profile.FanMode == GpuFanMode.Static && profile.FanStaticPercent is { } fp)
                {
                    var change = new PendingChange(OcControlNames.FanSpeed,
                        () => _controller.SetFanStaticPercent(fp), "auto-switch previous", $"{fp}%");
                    var previousWindow = _safety.ConfirmationSeconds;
                    _safety.ConfirmationSeconds = AutoApplyConfirmationSeconds;
                    try
                    {
                        if (_safety.ApplyFanBatchAsync(change, fanWasAuto: false, OverclockChangeSource.GameAutoApply)
                            .GetAwaiter().GetResult())
                        {
                            _safety.ConfirmSilently();
                            WaitForSafetyIdle(TimeSpan.FromSeconds(AutoApplyConfirmationSeconds + UnattendedWaitSlackSeconds));
                        }
                    }
                    finally
                    {
                        _safety.ConfirmationSeconds = previousWindow;
                    }
                }
                else if (profile.FanMode == GpuFanMode.Curve && profile.FanCurvePoints.Count > 0 && _fanCurve is not null)
                {
                    var pts = profile.FanCurvePoints.ToArray();
                    _fanCurve.Start(pts, ReadGpuTemp);
                    _log.Log(new AppliedChangeLogEntry
                    {
                        Timestamp = DateTime.Now,
                        ControlName = "Fan mode",
                        OldValue = "auto-switch previous",
                        NewValue = $"curve ({pts.Length} points)",
                        Source = OverclockChangeSource.GameAutoApply,
                        Result = OverclockChangeResult.Success,
                    });
                }
                else
                {
                    var res = _controller.RestoreFanAuto();
                    _log.Log(new AppliedChangeLogEntry
                    {
                        Timestamp = DateTime.Now,
                        ControlName = "Fan mode",
                        OldValue = "auto-switch previous",
                        NewValue = "auto (driver default)",
                        Source = OverclockChangeSource.GameAutoApply,
                        Result = res.IsSuccess ? OverclockChangeResult.Success : OverclockChangeResult.Failed,
                        FailureReason = res.Detail,
                    });
                }
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"auto-apply fan half failed: {ex.GetType().Name}");
            }
        }

        private int? ReadGpuTemp()
        {
            try { return _controller.ReadTelemetry().Value?.GpuTempC; }
            catch { return null; }
        }

        /// <summary>
        /// Default-or-stock revert. Single-level only (_inFallback): if the
        /// fallback itself fails there is nothing safer to try unattended -
        /// log it and surface it.
        /// </summary>
        private bool FallbackToDefault(string reason)
        {
            lock (_gate)
            {
                if (_inFallback) return false;
                _inFallback = true;
            }
            try
            {
                var fallback = ResolveDefaultProfile() ?? BuildStockProfile();
                if (fallback is null)
                {
                    Log("Game auto-switch", "fallback", "-",
                        OverclockChangeResult.Failed, $"no default profile and stock construction failed ({reason})");
                    Publish(null, null, $"Auto-switch fallback failed ({reason}): no default profile available.");
                    return false;
                }
                bool isStock = fallback.Name.StartsWith("Stock", StringComparison.Ordinal);
                _trace?.Invoke($"auto-apply fallback ({reason}): applying {(isStock ? "stock" : "default")} '{fallback.Name}'");
                // Single-level only: the no-fallback core below never recurses.
                bool ok = ApplyUnattendedNoFallback(fallback);
                if (ok)
                {
                    // The GPU is on the fallback now - the active anchor must
                    // follow it, so a later game-exit doesn't "revert" again.
                    lock (_gate)
                    {
                        _activeBinding = null;
                        _activeProfile = fallback;
                        _activePid = 0;
                    }
                    Publish(fallback.Name, null, $"{reason} - now on {(isStock ? "stock (no overclock)" : $"default '{fallback.Name}'")}.");
                }
                return ok;
            }
            finally
            {
                lock (_gate) _inFallback = false;
            }
        }

        /// <summary>Same as ApplyUnattended but failure just logs (no further fallback).</summary>
        private bool ApplyUnattendedNoFallback(OverclockProfile profile)
        {
            lock (_gate)
            {
                if (_applyInFlight || _safety.CurrentState != SafetyState.Idle) return false;
                _applyInFlight = true;
            }
            try
            {
                var caps = _controller.ReadCapabilities();
                var c = caps.IsSuccess ? caps.Value : null;
                if (c is null) return false;
                GpuVoltageFrequencyCurve? liveCurve = null;
                if (c.VfCurveSupported)
                {
                    var cv = _controller.ReadVoltageFrequencyCurve();
                    if (cv.IsSuccess) liveCurve = cv.Value;
                }
                var batch = ProfileBatchBuilder.Build(
                    _controller, c, profile, _ => "auto-switch previous",
                    liveCurve, out _);
                if (batch.Count == 0) return true;
                var previousWindow = _safety.ConfirmationSeconds;
                _safety.ConfirmationSeconds = AutoApplyConfirmationSeconds;
                try
                {
                    var applied = _safety.ApplyBatchAsync(batch, OverclockChangeSource.GameAutoApply)
                        .GetAwaiter().GetResult();
                    if (!applied)
                    {
                        Log("Game auto-switch", "fallback", profile.Name,
                            OverclockChangeResult.Failed, "fallback write refused by the driver");
                        Publish(null, null, $"Auto-switch fallback failed: the driver refused '{profile.Name}'.");
                        return false;
                    }
                    _safety.ConfirmSilently();
                    int window = _safety.GetConfirmationSecondsFor(batch.Select(b => b.ControlName));
                    WaitForSafetyIdle(TimeSpan.FromSeconds(window + UnattendedWaitSlackSeconds));
                    // Outcome publishing belongs to the caller (FallbackToDefault).
                    return true;
                }
                finally
                {
                    _safety.ConfirmationSeconds = previousWindow;
                }
            }
            finally
            {
                lock (_gate) _applyInFlight = false;
            }
        }

        /// <summary>
        /// The designated default profile when trustworthy: manually
        /// validated, or stock-like (no OC values - nothing risky to apply
        /// blind). Otherwise null → the caller builds synthetic stock.
        /// </summary>
        private OverclockProfile? ResolveDefaultProfile()
        {
            try
            {
                var name = _profiles.GetDefaultProfileName();
                if (name is null) return null;
                var profile = _profiles.LoadAll().FirstOrDefault(p => p.Name == name);
                if (profile is null) return null;
                if (profile.HasBeenManuallyValidated || IsStockLike(profile)) return profile;
                _trace?.Invoke($"fallback: default '{name}' is not manually validated - using stock instead");
                return null;
            }
            catch { return null; }
        }

        private static bool IsStockLike(OverclockProfile p)
            => (p.CoreOffsetMHz is null or 0)
               && (p.MemOffsetMHz is null or 0)
               && (p.PowerLimitPercent is null or 100)
               && p.TempLimitC is null
               && (p.VfCurveOffsets is null || p.VfCurveOffsets.All(o => o == 0))
               && (p.GlobalVoltageBoostOffsetMHz is null or 0)
               && p.FanMode == GpuFanMode.Auto;

        /// <summary>Synthetic "no overclock" profile: zero offsets, 100% power, zeroed curve.</summary>
        private OverclockProfile? BuildStockProfile()
        {
            try
            {
                var stock = new OverclockProfile
                {
                    Name = "Stock (auto-switch fallback)",
                    CoreOffsetMHz = 0,
                    MemOffsetMHz = 0,
                    PowerLimitPercent = 100,
                    FanMode = GpuFanMode.Auto,
                };
                var caps = _controller.ReadCapabilities();
                if (caps.IsSuccess && caps.Value?.VfCurveSupported == true)
                {
                    var cv = _controller.ReadVoltageFrequencyCurve();
                    if (cv.IsSuccess && cv.Value is { Count: > 0 })
                        stock.VfCurveOffsets = new List<int>(new int[cv.Value.Count]);
                }
                return stock;
            }
            catch { return null; }
        }

        private void ApplyDefaultOrStock(string reason)
        {
            // FallbackToDefault publishes the outcome itself.
            FallbackToDefault(reason);
        }

        private void Log(string control, string oldValue, string newValue,
            OverclockChangeResult result, string? failureReason)
        {
            _log.Log(new AppliedChangeLogEntry
            {
                Timestamp = DateTime.Now,
                ControlName = control,
                OldValue = oldValue,
                NewValue = newValue,
                Source = OverclockChangeSource.GameAutoApply,
                Result = result,
                FailureReason = failureReason,
            });
        }

        private void Publish(string? activeProfile, string? activeExe, string message)
        {
            AutoApplyStatus status;
            lock (_gate)
            {
                _current = new AutoApplyStatus(
                    activeProfile ?? _current.ActiveProfileName,
                    activeExe,
                    message, DateTime.Now,
                    _watcher.IsRunning ? _watcher.ModeDescription : "watcher stopped");
                status = _current;
            }
            try { StatusChanged?.Invoke(status); } catch { }
            _trace?.Invoke("auto-switch: " + message);
        }

        /// <summary>Blocks the background apply task until the machine is idle (bounded).</summary>
        private void WaitForSafetyIdle(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (_safety.CurrentState == SafetyState.Idle) return;
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _started = false;
            }
            try
            {
                _watcher.ProcessStarted -= HandleProcessStarted;
                _watcher.ProcessStopped -= HandleProcessStopped;
                _watcher.Stop();
                if (_ownsWatcher) _watcher.Dispose();
            }
            catch { }
        }
    }
}
