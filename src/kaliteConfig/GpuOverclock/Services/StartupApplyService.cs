using System;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class StartupApplyService
    {
        private readonly IGpuTuningController _controller;
        private readonly SafetyRevertService _safety;
        private readonly ProfileStorageService _profiles;
        private readonly OverclockChangeLogger _changeLog;
        private readonly Action<string>? _trace;

        public int StartupConfirmationSeconds { get; set; } = 5;

        public StartupApplyService(
            IGpuTuningController controller,
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

                    _safety.ConfirmSilently();
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
