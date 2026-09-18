using System;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Startup reapply (spec 6): NVAPI state does not survive reboots or driver
    /// reloads, so the designated default profile is re-applied shortly after
    /// login via the elevated Task Scheduler task (StartupTaskService).
    ///
    /// SAFETY TRADEOFF (stated explicitly per spec): at login the user cannot
    /// see an interactive confirmation prompt — the window would block a
    /// headless session. Instead the profile is re-applied through the SAME
    /// safety state machine with a SHORT window (5 s vs 15 s) that expires
    /// on its own: the TDR watchdog is armed the entire time, so a driver
    /// reset reverts immediately; a clean expiry confirms the batch.
    /// Only profiles marked pre-boot-validated (ConfirmedAt non-null — their
    /// values survived a full interactive confirmation window before) are
    /// eligible for startup reapply.
    /// </summary>
    public sealed class StartupApplyService
    {
        private readonly INvidiaGpuController _controller;
        private readonly SafetyRevertService _safety;
        private readonly ProfileStorageService _profiles;
        private readonly OverclockChangeLogger _changeLog;
        private readonly Action<string>? _trace;

        /// <summary>Shortened confirmation window for headless startup applies.</summary>
        public int StartupConfirmationSeconds { get; set; } = 5;

        public StartupApplyService(
            INvidiaGpuController controller,
            SafetyRevertService safety,
            ProfileStorageService profiles,
            OverclockChangeLogger changeLog,
            Action<string>? trace = null)
        {
            _controller = controller;
            _safety = safety;
            _profiles = profiles;
            _changeLog = changeLog;
            _trace = trace;
        }

        /// <summary>
        /// Entry point for the --apply-overclock-startup argument path: reapplies
        /// the designated default profile if one is designated and pre-boot
        /// validated. Never throws; the log records the outcome either way.
        /// </summary>
        public async Task ApplyDefaultProfileAtStartupAsync()
        {
            try
            {
                var name = _profiles.GetDefaultProfileName();
                if (name is null)
                {
                    _trace?.Invoke("startup reapply: no default profile designated");
                    return;
                }

                var profile = _profiles.LoadAll().FirstOrDefault(p => p.Name == name);
                if (profile is null)
                {
                    _trace?.Invoke($"startup reapply: default profile '{name}' not found");
                    return;
                }

                if (profile.ConfirmedAt is null)
                {
                    // Not pre-boot validated — the spec's known-safe flag is
                    // absent, so the values have never survived a confirmation
                    // window. Refuse rather than risk a black screen at login.
                    _trace?.Invoke($"startup reapply: '{name}' is not pre-boot validated (no ConfirmedAt); skipping");
                    _changeLog.Log(new AppliedChangeLogEntry
                    {
                        Timestamp = DateTime.Now,
                        ControlName = "Startup reapply",
                        OldValue = "-",
                        NewValue = profile.Name,
                        Source = OverclockChangeSource.StartupApply,
                        Result = OverclockChangeResult.Failed,
                        FailureReason = "profile is not pre-boot validated",
                    });
                    return;
                }

                await ApplyProfileAsync(profile);
            }
            catch (Exception ex)
            {
                _trace?.Invoke($"startup reapply failed: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Applies the profile as ONE batch through the safety machine with the
        /// shortened headless window; marks the profile validated when the
        /// window closes cleanly.
        /// </summary>
        public async Task ApplyProfileAsync(OverclockProfile profile)
        {
            var init = _controller.Initialize();
            if (!init.IsSuccess)
            {
                _trace?.Invoke($"startup reapply: NVAPI init failed ({init.ErrorKind})");
                return;
            }

            var caps = _controller.ReadCapabilities();
            var c = caps.IsSuccess ? caps.Value : null;
            if (c is null)
            {
                _trace?.Invoke("startup reapply: capabilities unavailable");
                return;
            }

            var batch = ProfileBatchBuilder.Build(
                _controller, c, profile,
                _ => "driver current",
                ReadLiveCurveIfSupported(c),
                out string? curveSkipped);
            if (curveSkipped is not null) _trace?.Invoke($"startup reapply: {curveSkipped}");

            if (batch.Count == 0)
            {
                _trace?.Invoke($"startup reapply: '{profile.Name}' has no values this GPU supports");
                return;
            }

            var previousWindow = _safety.ConfirmationSeconds;
            _safety.ConfirmationSeconds = StartupConfirmationSeconds;
            try
            {
                var reverted = false;
                void OnStateChanged(object? s, SafetyStateChangedEventArgs e)
                {
                    if (e.State == SafetyState.Reverting) reverted = true;
                }
                _safety.StateChanged += OnStateChanged;

                try
                {
                    var applied = await _safety.ApplyBatchAsync(batch, OverclockChangeSource.StartupApply);
                    if (!applied)
                    {
                        _trace?.Invoke("startup reapply: the driver refused one or more profile values");
                        return;
                    }

                    _safety.ConfirmSilently(); // headless: let the short window run out
                    // Curve batches run a LONGER window (per-control overrides) —
                    // wait for the batch's actual window, not the global default.
                    int window = _safety.GetConfirmationSecondsFor(batch.Select(b => b.ControlName));
                    var closed = await WaitForSafetyIdleAsync(TimeSpan.FromSeconds(window + 10));

                    if (closed && !reverted)
                    {
                        profile.ConfirmedAt = DateTime.Now;
                        profile.LastAppliedAt = DateTime.Now;
                        _profiles.Save(profile);
                        _trace?.Invoke($"startup reapply: '{profile.Name}' applied and confirmed ({batch.Count} controls)");
                    }
                    else
                    {
                        _trace?.Invoke($"startup reapply: '{profile.Name}' was reverted (driver reset during the window)");
                    }
                }
                finally
                {
                    _safety.StateChanged -= OnStateChanged;
                }
            }
            finally
            {
                _safety.ConfirmationSeconds = previousWindow;
            }
        }

        /// <summary>Reads the live V/F curve when supported; null otherwise (curve skipped, never fatal).</summary>
        private GpuVoltageFrequencyCurve? ReadLiveCurveIfSupported(GpuCapabilities c)
        {
            if (!c.VfCurveSupported) return null;
            var curve = _controller.ReadVoltageFrequencyCurve();
            return curve.IsSuccess ? curve.Value : null;
        }

        private async Task<bool> WaitForSafetyIdleAsync(TimeSpan timeout)        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (_safety.CurrentState is SafetyState.Idle) return true;
                await Task.Delay(100);
            }
            return false;
        }

    }
}
