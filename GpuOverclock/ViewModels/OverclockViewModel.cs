using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace kaliteConfig.GpuOverclock.ViewModels
{
    /// <summary>
    /// Overclock module ViewModel. All NVAPI work happens off the UI thread
    /// (polling loop, Task.Run around controller calls); everything user-facing
    /// is marshaled back through the DispatcherQueue.
    /// </summary>
    public partial class OverclockViewModel : ObservableObject
    {
        private readonly GpuOverclockModule _module;
        private readonly DispatcherQueue _dispatcher;
        private DispatcherQueueTimer? _debounceTimer;
        private readonly Dictionary<string, PendingWrite> _dirty = new();
        private readonly record struct PendingWrite(string ControlName, Func<GpuResult> Apply, string OldDisplay, string NewDisplay);

        private sealed record Anchors(int CoreMhz, int MemMhz, double PowerPct, int? TempC);

        private Anchors _anchors = new(0, 0, 100, null);

        public GpuTelemetryPollingService Telemetry => _module.Telemetry;
        public SafetyRevertService Safety => _module.Safety;

        public ObservableCollection<AppliedChangeLogEntry> LogEntries { get; } = new();
        public ObservableCollection<OverclockProfile> Profiles { get; } = new();
        public ObservableCollection<FanCurvePointRow> CurvePoints { get; } = new();

        public OverclockViewModel(DispatcherQueue? dispatcher = null)
        {
            _module = GpuOverclockModule.Instance;
            _dispatcher = dispatcher ?? App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

            _module.Telemetry.Update += OnTelemetryUpdate;
            _module.Safety.StateChanged += OnSafetyStateChanged;
            _module.FanCurve.Stopped += OnFanCurveStopped;

            CurvePoints.Add(new FanCurvePointRow(40, 30));
            CurvePoints.Add(new FanCurvePointRow(60, 60));
            CurvePoints.Add(new FanCurvePointRow(75, 85));
            CurvePoints.Add(new FanCurvePointRow(85, 100));
        }

        // ---------------- availability / identity ----------------

        [ObservableProperty]
        public partial bool IsSupported { get; private set; }

        [ObservableProperty]
        public partial string UnsupportedMessage { get; private set; } = "";

        [ObservableProperty]
        public partial string GpuTitle { get; private set; } = "";

        [ObservableProperty]
        public partial bool IsBusy { get; private set; }

        [ObservableProperty]
        public partial string StatusText { get; private set; } = "";

        // ---------------- telemetry ----------------

        [ObservableProperty]
        public partial bool TelemetryAvailable { get; private set; }

        [ObservableProperty]
        public partial string TelemetryErrorText { get; private set; } = "";

        [ObservableProperty]
        public partial string CoreClockText { get; private set; } = "—";

        [ObservableProperty]
        public partial string MemClockText { get; private set; } = "—";

        [ObservableProperty]
        public partial string TempText { get; private set; } = "—";

        [ObservableProperty]
        public partial string HotspotText { get; private set; } = "";

        [ObservableProperty]
        public partial string PowerText { get; private set; } = "—";

        [ObservableProperty]
        public partial string FanText { get; private set; } = "—";

        [ObservableProperty]
        public partial string UsageText { get; private set; } = "—";

        [ObservableProperty]
        public partial string VramText { get; private set; } = "—";

        [ObservableProperty]
        public partial string PcieText { get; private set; } = "—";

        private void OnTelemetryUpdate(TelemetryUpdate update)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (update.Health == TelemetryHealth.Unavailable)
                {
                    // Distinct unavailable state — never show stale/zeroed values as fact.
                    TelemetryAvailable = false;
                    TelemetryErrorText = string.IsNullOrEmpty(update.ErrorDetail)
                        ? "Telemetry unavailable — the GPU or driver is not responding."
                        : $"Telemetry unavailable ({update.ErrorDetail}).";
                    return;
                }

                var s = update.Snapshot!;
                _latestTempC = s.GpuTempC; // feeds the fan-curve loop
                TelemetryAvailable = true;
                TelemetryErrorText = "";
                CoreClockText = s.CoreClockMHz is null ? "—" : $"{s.CoreClockMHz.Value:0} MHz";
                MemClockText = s.MemClockMHz is null ? "—" : $"{s.MemClockMHz.Value:0} MHz";
                TempText = s.GpuTempC is null ? "—" : $"{s.GpuTempC.Value} °C";
                HotspotText = s.HotspotTempC is null ? "" : $"hotspot {s.HotspotTempC.Value} °C";
                PowerText = FormatPower(s);
                FanText = s.FanPercent is null ? "—"
                    : $"{s.FanPercent.Value}%{(s.FanRpm is null ? "" : $" ({s.FanRpm.Value} RPM)")}";
                UsageText = s.GpuUsagePercent is null ? "—" : $"{s.GpuUsagePercent.Value}%";
                VramText = s.VramUsageMb is null ? "—" : $"{s.VramUsageMb.Value:0} MB";
                PcieText = s.PcieGen is null ? "—" : $"Gen {s.PcieGen.Value} x{s.PcieWidth ?? 0}";
            });
        }

        private static string FormatPower(GpuTelemetrySnapshot s)
        {
            // Prefer absolute watts (NVML); fall back to %-of-limit (NVAPI native).
            if (s.PowerDrawW is not null) return $"{s.PowerDrawW.Value:0.#} W";
            if (s.PowerDrawPercentOfLimit is not null) return $"{s.PowerDrawPercentOfLimit.Value:0}% of limit";
            return "—";
        }

        // ---------------- capabilities + control values ----------------

        [ObservableProperty]
        public partial bool HasCoreOffset { get; private set; }

        [ObservableProperty]
        public partial double CoreOffsetMin { get; private set; }

        [ObservableProperty]
        public partial double CoreOffsetMax { get; private set; }

        [ObservableProperty]
        public partial double CoreOffsetValue { get; private set; }

        [ObservableProperty]
        public partial bool HasMemOffset { get; private set; }

        [ObservableProperty]
        public partial double MemOffsetMin { get; private set; }

        [ObservableProperty]
        public partial double MemOffsetMax { get; private set; }

        [ObservableProperty]
        public partial double MemOffsetValue { get; private set; }

        [ObservableProperty]
        public partial bool HasPowerLimit { get; private set; }

        [ObservableProperty]
        public partial double PowerLimitMin { get; private set; }

        [ObservableProperty]
        public partial double PowerLimitMax { get; private set; }

        [ObservableProperty]
        public partial double PowerLimitValue { get; private set; }

        [ObservableProperty]
        public partial bool HasTempLimit { get; private set; }

        [ObservableProperty]
        public partial double TempLimitMin { get; private set; }

        [ObservableProperty]
        public partial double TempLimitMax { get; private set; }

        [ObservableProperty]
        public partial double TempLimitValue { get; private set; }

        // Display strings for the slider rows (kept in sync via change hooks below).
        public string CoreOffsetDisplay => HasCoreOffset ? $"{CoreOffsetValue:+0;-0;0} MHz" : "";
        public string MemOffsetDisplay => HasMemOffset ? $"{MemOffsetValue:+0;-0;0} MHz" : "";
        public string PowerLimitDisplay => HasPowerLimit ? $"{PowerLimitValue:0.#}%" : "";
        public string TempLimitDisplay => HasTempLimit ? $"{TempLimitValue:0} °C" : "";

        partial void OnCoreOffsetValueChanged(double value) => OnPropertyChanged(nameof(CoreOffsetDisplay));
        partial void OnMemOffsetValueChanged(double value) => OnPropertyChanged(nameof(MemOffsetDisplay));
        partial void OnPowerLimitValueChanged(double value) => OnPropertyChanged(nameof(PowerLimitDisplay));
        partial void OnTempLimitValueChanged(double value) => OnPropertyChanged(nameof(TempLimitDisplay));

        [RelayCommand]
        public async Task InitializeAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Detecting NVIDIA GPU…";
            try
            {
                var init = await Task.Run(_module.Controller.Initialize);
                if (!init.IsSuccess)
                {
                    SetUnsupported(OverclockErrorMessages.For(init.ErrorKind));
                    return;
                }

                var id = await Task.Run(_module.Controller.GetIdentity);
                if (!id.IsSuccess)
                {
                    SetUnsupported(OverclockErrorMessages.For(id.ErrorKind));
                    return;
                }

                _module.Telemetry.Start();
                await LoadCapabilitiesAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Called on GPU re-detect: forgets the cached handle and re-reads ranges.</summary>
        public async Task RefreshAsync()
        {
            _module.Controller.InvalidateGpu();
            if (_module.Controller.IsInitialized)
            {
                _module.Telemetry.Start();
                await LoadCapabilitiesAsync();
            }
            else
            {
                await InitializeAsync();
            }
        }

        private void SetUnsupported(string message)
        {
            IsSupported = false;
            UnsupportedMessage = message;
            _module.Telemetry.Update -= OnTelemetryUpdate;
        }

        private async Task LoadCapabilitiesAsync()
        {
            var caps = await Task.Run(_module.Controller.ReadCapabilities);
            if (!caps.IsSuccess || caps.Value is null)
            {
                SetUnsupported(OverclockErrorMessages.For(caps.ErrorKind));
                return;
            }

            var c = caps.Value;
            GpuTitle = $"{c.GpuName}  ·  driver {c.DriverVersion}";

            var offs = await Task.Run(_module.Controller.ReadCurrentOffsets);
            var lims = await Task.Run(_module.Controller.ReadCurrentLimits);
            int coreCur = offs.IsSuccess ? offs.Value.CoreOffsetMHz : 0;
            int memCur = offs.IsSuccess ? offs.Value.MemOffsetMHz : 0;
            double powerCur = lims.IsSuccess ? lims.Value.PowerLimitPercent : c.CurrentPowerLimitPercent ?? 100;
            int? tempCur = lims.IsSuccess ? lims.Value.TempLimitC : c.CurrentTempLimitC;
            _anchors = new Anchors(coreCur, memCur, powerCur, tempCur);

            HasCoreOffset = c.CoreOffsetRangeMHz is not null;
            if (c.CoreOffsetRangeMHz is { } cr)
            {
                CoreOffsetMin = cr.Minimum; CoreOffsetMax = cr.Maximum;
                CoreOffsetValue = cr.Clamp(coreCur);
            }
            HasMemOffset = c.MemOffsetRangeMHz is not null;
            if (c.MemOffsetRangeMHz is { } mr)
            {
                MemOffsetMin = mr.Minimum; MemOffsetMax = mr.Maximum;
                MemOffsetValue = mr.Clamp(memCur);
            }
            HasPowerLimit = c.PowerLimitRangePercent is not null;
            if (c.PowerLimitRangePercent is { } pr)
            {
                PowerLimitMin = pr.Minimum; PowerLimitMax = pr.Maximum;
                PowerLimitValue = pr.Clamp(powerCur);
            }
            HasTempLimit = c.TempLimitRangeC is not null; // control hidden entirely when null
            if (c.TempLimitRangeC is { } tr)
            {
                TempLimitMin = tr.Minimum; TempLimitMax = tr.Maximum;
                TempLimitValue = tr.Clamp(tempCur ?? tr.Maximum);
            }
            IsSupported = c.AnyControlSupported || c.FanControlSupported;
            UnsupportedMessage = IsSupported ? "" : "This GPU exposes no supported overclock controls.";
            FanControlAvailable = c.FanControlSupported;

            // Display strings must be re-notified here explicitly: when the
            // current offset is 0 the value setter (0 → 0) fires no change
            // notification, and the text would stay at its initial empty binding.
            NotifySliderDisplays();

            StatusText = "";
        }

        private void NotifySliderDisplays()
        {
            OnPropertyChanged(nameof(CoreOffsetDisplay));
            OnPropertyChanged(nameof(MemOffsetDisplay));
            OnPropertyChanged(nameof(PowerLimitDisplay));
            OnPropertyChanged(nameof(TempLimitDisplay));
        }

        // ---------------- writes (debounced batch -> safety machine) ----------------

        public void OnCoreOffsetCommitted(double value)
        {
            // Live preview: the readout label follows the slider thumb during the
            // drag instead of only updating after the driver write round-trips.
            CoreOffsetValue = value;
            QueueWrite(OcControlNames.CoreClockOffset, () => _module.Controller.SetCoreOffsetMhz((int)value),
                $"{_anchors.CoreMhz:+0;-0;0} MHz", $"{(int)value:+0;-0;0} MHz");
        }

        public void OnMemOffsetCommitted(double value)
        {
            MemOffsetValue = value;
            QueueWrite(OcControlNames.MemoryClockOffset, () => _module.Controller.SetMemoryOffsetMhz((int)value),
                $"{_anchors.MemMhz:+0;-0;0} MHz", $"{(int)value:+0;-0;0} MHz");
        }

        public void OnPowerLimitCommitted(double value)
        {
            PowerLimitValue = value;
            QueueWrite(OcControlNames.PowerLimit, () => _module.Controller.SetPowerLimitPercent(value),
                $"{_anchors.PowerPct:0.#}%", $"{value:0.#}%");
        }

        public void OnTempLimitCommitted(double value)
        {
            TempLimitValue = value;
            QueueWrite(OcControlNames.TemperatureLimit, () => _module.Controller.SetTempLimitC((int)value),
                _anchors.TempC is null ? "driver default" : $"{_anchors.TempC} °C", $"{(int)value} °C");
        }

        /// <summary>
        /// Debounced batching: changes to any controls within the debounce
        /// window become ONE safety batch with one confirmation countdown.
        /// The timer restarts on every queued write, so the flush fires once,
        /// after the LAST change (e.g. when a slider drag settles).
        /// </summary>
        private void QueueWrite(string controlName, Func<GpuResult> apply, string oldDisplay, string newDisplay)
        {
            if (!IsSupported) return;
            _dirty[controlName] = new PendingWrite(controlName, apply, oldDisplay, newDisplay);
            _debounceTimer ??= _dispatcher.CreateTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(600);
            _debounceTimer.IsRepeating = false;
            _debounceTimer.Tick -= FlushDirty;
            _debounceTimer.Tick += FlushDirty;
            _debounceTimer.Stop(); // restart the window on every change (trailing debounce)
            _debounceTimer.Start();
        }

        private void FlushDirty(object? sender, object e)
        {
            _debounceTimer?.Stop();
            if (_dirty.Count == 0) return;

            // A confirmation window/revert is still in flight: defer the flush
            // rather than failing — this is NOT a driver refusal.
            if (_module.Safety.CurrentState != SafetyState.Idle)
            {
                _debounceTimer?.Start();
                return;
            }

            var batch = _dirty.Values
                .Select(w => new PendingChange(w.ControlName, w.Apply, w.OldDisplay, w.NewDisplay))
                .ToArray();
            _dirty.Clear();

            var source = OverclockChangeSource.Manual;
            _ = Task.Run(async () =>
            {
                var ok = await _module.Safety.ApplyBatchAsync(batch, source);
                if (!ok)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var last = batch[^1];
                        StatusText = $"The driver refused the change to {last.ControlName.ToLowerInvariant()}.";
                    });
                }
            });
        }

        /// <summary>
        /// Re-reads the driver's current offsets/limits after a revert (or any
        /// external change) and moves the slider positions to match.
        /// </summary>
        private async Task ResyncControlsFromHardwareAsync()
        {
            var offs = await Task.Run(_module.Controller.ReadCurrentOffsets);
            var lims = await Task.Run(_module.Controller.ReadCurrentLimits);
            if (offs.IsSuccess)
            {
                CoreOffsetValue = Math.Clamp(offs.Value.CoreOffsetMHz, CoreOffsetMin, CoreOffsetMax);
                MemOffsetValue = Math.Clamp(offs.Value.MemOffsetMHz, MemOffsetMin, MemOffsetMax);
                _anchors = _anchors with { CoreMhz = offs.Value.CoreOffsetMHz, MemMhz = offs.Value.MemOffsetMHz };
            }
            if (lims.IsSuccess)
            {
                if (HasPowerLimit) PowerLimitValue = Math.Clamp(lims.Value.PowerLimitPercent, PowerLimitMin, PowerLimitMax);
                if (HasTempLimit && lims.Value.TempLimitC is { } t) TempLimitValue = Math.Clamp(t, TempLimitMin, TempLimitMax);
                _anchors = _anchors with { PowerPct = lims.Value.PowerLimitPercent, TempC = lims.Value.TempLimitC };
            }
            NotifySliderDisplays();
        }

        private void OnSafetyStateChanged(object? sender, SafetyStateChangedEventArgs e)
        {
            _dispatcher.TryEnqueue(() =>
            {
                SafetyStateValue = e.State;
                CountdownSeconds = e.CountdownSecondsRemaining;
                ShowCountdown = e.State == SafetyState.AwaitingConfirmation;
                SafetyBannerText = e.State switch
                {
                    SafetyState.Applying => "Applying changes…",
                    SafetyState.AwaitingConfirmation => $"Keep these settings? Reverting in {e.CountdownSecondsRemaining} s.",
                    SafetyState.Reverting => "Reverting to your previous settings…",
                    _ => "",
                };
                if (e.State == SafetyState.Idle && !string.IsNullOrEmpty(e.RevertReason))
                {
                    StatusText = e.RevertReason == "driver reset detected"
                        ? "Your last change caused a driver reset and was automatically reverted."
                        : "Changes were reverted.";
                    // Sliders must show restored hardware values, not the user's
                    // attempted values. Fire-and-forget resync.
                    _ = ResyncControlsFromHardwareAsync();
                }
                else if (e.State == SafetyState.AwaitingConfirmation)
                {
                    StatusText = "";
                }
                RefreshLog();
            });
        }

        // ---------------- risk acceptance gate ----------------

        /// <summary>
        /// Session-scoped: the extreme-caution gate covers the whole overclock
        /// card until explicitly accepted. Deliberately NOT persisted — every
        /// app start re-arms the gate so the risk acknowledgment is never
        /// "set and forgotten".
        /// </summary>
        [ObservableProperty]
        public partial bool RiskAccepted { get; private set; }

        [RelayCommand]
        public void AcceptRisk() => RiskAccepted = true;

        // ---------------- safety UI ----------------

        [ObservableProperty]
        public partial SafetyState SafetyStateValue { get; private set; }

        [ObservableProperty]
        public partial bool ShowCountdown { get; private set; }

        [ObservableProperty]
        public partial int CountdownSeconds { get; private set; }

        [ObservableProperty]
        public partial string SafetyBannerText { get; private set; } = "";

        [RelayCommand]
        public void ConfirmChanges() => _module.Safety.Confirm();

        [RelayCommand]
        public void RevertNow() => _module.Safety.RevertNow();

        // ---------------- fan control ----------------

        [ObservableProperty]
        public partial GpuFanMode FanMode { get; private set; } = GpuFanMode.Auto;

        [ObservableProperty]
        public partial double FanStaticPercent { get; set; } = 50;

        [ObservableProperty]
        public partial bool FanControlAvailable { get; private set; }

        // Radio-button state; the setters funnel into the same guarded commands
        // the buttons would call, so unchecking fires nothing.
        public bool FanAutoChecked
        {
            get => FanMode == GpuFanMode.Auto;
            set { if (value) _ = SetFanAutoAsync(); }
        }

        public bool FanStaticChecked
        {
            get => FanMode == GpuFanMode.Static;
            set { if (value) _ = SetFanStaticAsync(); }
        }

        public bool FanCurveChecked
        {
            get => FanMode == GpuFanMode.Curve;
            set { if (value) StartFanCurve(); }
        }

        partial void OnFanModeChanged(GpuFanMode value)
        {
            OnPropertyChanged(nameof(FanAutoChecked));
            OnPropertyChanged(nameof(FanStaticChecked));
            OnPropertyChanged(nameof(FanCurveChecked));
        }

        [RelayCommand]
        public async Task SetFanAutoAsync()
        {
            if (Safety.CurrentState != SafetyState.Idle) return;
            FanMode = GpuFanMode.Auto;
            await _module.FanCurve.StopAsync("switched to auto");
            // Explicit reset call through the safety machine so a stale forced
            // speed can never persist silently.
            var res = await Task.Run(_module.Controller.RestoreFanAuto);
            _module.ChangeLog.Log(new AppliedChangeLogEntry
            {
                Timestamp = DateTime.Now,
                ControlName = "Fan mode",
                OldValue = "manual/curve",
                NewValue = "auto (driver default)",
                Source = OverclockChangeSource.Manual,
                Result = res.IsSuccess ? OverclockChangeResult.Success : OverclockChangeResult.Failed,
                FailureReason = res.Detail,
            });
        }

        [RelayCommand]
        public async Task SetFanStaticAsync()
        {
            if (Safety.CurrentState != SafetyState.Idle) return;
            await _module.FanCurve.StopAsync("switched to static");
            FanMode = GpuFanMode.Static;
            var pct = (int)FanStaticPercent;
            var change = new PendingChange(OcControlNames.FanSpeed,
                () => _module.Controller.SetFanStaticPercent(pct),
                "auto", $"{pct}%");
            await _module.Safety.ApplyFanBatchAsync(change, fanWasAuto: false, OverclockChangeSource.Manual);
        }

        [RelayCommand]
        public void StartFanCurve()
        {
            if (Safety.CurrentState != SafetyState.Idle) return;
            var points = CurvePoints.Select(p => new FanCurvePoint(p.TempC, p.FanPercent)).ToArray();
            if (points.Length == 0) return;
            FanMode = GpuFanMode.Curve;
            _module.FanCurve.Start(points, () => _latestTempC);
            _module.ChangeLog.Log(new AppliedChangeLogEntry
            {
                Timestamp = DateTime.Now,
                ControlName = "Fan mode",
                OldValue = "auto",
                NewValue = $"curve ({points.Length} points)",
                Source = OverclockChangeSource.Manual,
                Result = OverclockChangeResult.Success,
            });
        }

        private int? _latestTempC;

        private void OnFanCurveStopped(string? reason)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (FanMode == GpuFanMode.Curve) FanMode = GpuFanMode.Auto;
                if (!string.IsNullOrEmpty(reason)) StatusText = reason;
            });
        }

        partial void OnFanStaticPercentChanged(double value)
        {
            // Dragging the static slider while in Static mode re-applies through
            // the same debounced batch path.
            if (FanMode == GpuFanMode.Static)
                QueueWrite(OcControlNames.FanSpeed, () => _module.Controller.SetFanStaticPercent((int)value),
                    "previous %", $"{(int)value}%");
        }

        // ---------------- profiles ----------------

        [ObservableProperty]
        public partial OverclockProfile? SelectedProfile { get; set; }

        [ObservableProperty]
        public partial string NewProfileName { get; set; } = "";

        [ObservableProperty]
        public partial bool StartupReapplyEnabled { get; private set; }

        [RelayCommand]
        public void RefreshProfiles()
        {
            Profiles.Clear();
            foreach (var p in _module.Profiles.LoadAll()) Profiles.Add(p);
            StartupReapplyEnabled = _module.StartupTask.IsRegistered;
        }

        [RelayCommand]
        public void SaveProfile()
        {
            var name = string.IsNullOrWhiteSpace(NewProfileName)
                ? $"Profile {DateTime.Now:yyyy-MM-dd HH:mm}"
                : NewProfileName.Trim();
            var p = new OverclockProfile
            {
                Name = name,
                CoreOffsetMHz = (int)CoreOffsetValue,
                MemOffsetMHz = (int)MemOffsetValue,
                PowerLimitPercent = HasPowerLimit ? PowerLimitValue : null,
                TempLimitC = HasTempLimit ? (int)TempLimitValue : null,
                FanMode = FanMode,
                FanStaticPercent = (int)FanStaticPercent,
                FanCurvePoints = CurvePoints.Select(r => new FanCurvePoint(r.TempC, r.FanPercent)).ToList(),
            };
            _module.Profiles.Save(p);
            NewProfileName = "";
            RefreshProfiles();
        }

        /// <summary>
        /// Applies a profile as one batch through the same safety state machine —
        /// profile loads are never exempt (spec 6).
        /// </summary>
        [RelayCommand]
        public async Task ApplyProfileAsync(OverclockProfile? profile)
        {
            if (profile is null || Safety.CurrentState != SafetyState.Idle) return;

            var batch = new List<PendingChange>();
            if (profile.CoreOffsetMHz is { } core && HasCoreOffset)
                batch.Add(new PendingChange(OcControlNames.CoreClockOffset,
                    () => _module.Controller.SetCoreOffsetMhz(core),
                    $"{_anchors.CoreMhz:+0;-0;0} MHz", $"{core:+0;-0;0} MHz"));
            if (profile.MemOffsetMHz is { } mem && HasMemOffset)
                batch.Add(new PendingChange(OcControlNames.MemoryClockOffset,
                    () => _module.Controller.SetMemoryOffsetMhz(mem),
                    $"{_anchors.MemMhz:+0;-0;0} MHz", $"{mem:+0;-0;0} MHz"));
            if (profile.PowerLimitPercent is { } power && HasPowerLimit)
                batch.Add(new PendingChange(OcControlNames.PowerLimit,
                    () => _module.Controller.SetPowerLimitPercent(power),
                    $"{_anchors.PowerPct:0.#}%", $"{power:0.#}%"));
            if (profile.TempLimitC is { } temp && HasTempLimit)
                batch.Add(new PendingChange(OcControlNames.TemperatureLimit,
                    () => _module.Controller.SetTempLimitC(temp),
                    _anchors.TempC is null ? "driver default" : $"{_anchors.TempC} °C", $"{temp} °C"));

            if (batch.Count > 0)
            {
                var ok = await Task.Run(() => _module.Safety.ApplyBatchAsync(batch, OverclockChangeSource.ProfileApply));
                if (!ok)
                {
                    StatusText = "The driver refused one or more values from this profile.";
                    return;
                }
            }

            // Fan settings follow after the clock/limit batch confirmed-or-reverted
            // is handled by the machine; static mode applies through a fan batch.
            if (profile.FanMode == GpuFanMode.Static && profile.FanStaticPercent is { } fp)
            {
                FanStaticPercent = fp;
                await SetFanStaticAsync();
            }
            else if (profile.FanMode == GpuFanMode.Curve && profile.FanCurvePoints.Count > 0)
            {
                CurvePoints.Clear();
                foreach (var pt in profile.FanCurvePoints) CurvePoints.Add(new FanCurvePointRow(pt.TempC, pt.FanPercent));
                StartFanCurve();
            }
            else
            {
                await SetFanAutoAsync();
            }

            profile.LastAppliedAt = DateTime.Now;
            _module.Profiles.Save(profile);
        }

        [RelayCommand]
        public void DeleteProfile(OverclockProfile? profile)
        {
            if (profile is null) return;
            _module.Profiles.Delete(profile);
            RefreshProfiles();
        }

        [RelayCommand]
        public void ToggleStartupReapply()
        {
            if (_module.StartupTask.IsRegistered)
            {
                _module.StartupTask.Unregister();
                _module.ChangeLog.Log(new AppliedChangeLogEntry
                {
                    Timestamp = DateTime.Now, ControlName = "Startup reapply",
                    OldValue = "registered", NewValue = "unregistered",
                    Source = OverclockChangeSource.Manual, Result = OverclockChangeResult.Success,
                });
            }
            else
            {
                _module.StartupTask.Register();
            }
            StartupReapplyEnabled = _module.StartupTask.IsRegistered;
        }

        // ---------------- change log ----------------

        [RelayCommand]
        public void RefreshLog()
        {
            LogEntries.Clear();
            foreach (var e in _module.ChangeLog.Tail().Reverse()) LogEntries.Add(e);
        }

        [RelayCommand]
        public void OpenChangeLogFile()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _module.ChangeLog.LogFilePath,
                    UseShellExecute = true,
                });
            }
            catch { /* log file may not exist yet */ }
        }

        public void Teardown()
        {
            _module.Telemetry.Update -= OnTelemetryUpdate;
            _module.Safety.StateChanged -= OnSafetyStateChanged;
            _module.FanCurve.Stopped -= OnFanCurveStopped;
            _debounceTimer?.Stop();
        }
    }

    /// <summary>Editable row for the fan curve grid.</summary>
    public sealed class FanCurvePointRow : ObservableObject
    {
        private int _tempC;
        private int _fanPercent;

        public FanCurvePointRow(int tempC, int fanPercent)
        {
            _tempC = tempC;
            _fanPercent = fanPercent;
        }

        public int TempC { get => _tempC; set => SetProperty(ref _tempC, value); }
        public int FanPercent { get => _fanPercent; set => SetProperty(ref _fanPercent, value); }
    }
}
