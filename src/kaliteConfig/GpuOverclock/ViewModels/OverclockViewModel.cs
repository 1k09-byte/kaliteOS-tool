using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
            _module.GameAutoApply.StatusChanged += OnAutoApplyStatus;

            CurvePoints.Add(new FanCurvePointRow(40, 30));
            CurvePoints.Add(new FanCurvePointRow(60, 60));
            CurvePoints.Add(new FanCurvePointRow(75, 85));
            CurvePoints.Add(new FanCurvePointRow(85, 100));
        }

        [ObservableProperty]
        public partial bool IsSupported { get; private set; }

        [ObservableProperty]
        public partial string UnsupportedMessage { get; private set; } = "";

        [ObservableProperty]
        public partial string GpuTitle { get; private set; } = "";

        public bool IsSimulation => _module.IsSimulation;

        public string SimulationLabel => _module.SimulationLabel;

        [ObservableProperty]
        public partial bool IsBusy { get; private set; }

        [ObservableProperty]
        public partial string StatusText { get; private set; } = "";

        [ObservableProperty]
        public partial bool TelemetryAvailable { get; private set; }

        [ObservableProperty]
        public partial string TelemetryErrorText { get; private set; } = "";

        [ObservableProperty]
        public partial string CoreClockText { get; private set; } = "-";

        [ObservableProperty]
        public partial string MemClockText { get; private set; } = "-";

        [ObservableProperty]
        public partial string TempText { get; private set; } = "-";

        [ObservableProperty]
        public partial string HotspotText { get; private set; } = "";

        [ObservableProperty]
        public partial string PowerText { get; private set; } = "-";

        [ObservableProperty]
        public partial string FanText { get; private set; } = "-";

        [ObservableProperty]
        public partial string UsageText { get; private set; } = "-";

        [ObservableProperty]
        public partial string VramText { get; private set; } = "-";

        [ObservableProperty]
        public partial string PcieText { get; private set; } = "-";

        [ObservableProperty]
        public partial string MemTempText { get; private set; } = "-";

        [ObservableProperty]
        public partial string VoltageText { get; private set; } = "-";

        [ObservableProperty]
        public partial string PcieTputText { get; private set; } = "-";

        public ObservableCollection<TelemetryTileRow> TelemetryTiles { get; } = new();

        private void OnTelemetryUpdate(TelemetryUpdate update)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (update.Health == TelemetryHealth.Unavailable)
                {
                    TelemetryAvailable = false;
                    TelemetryErrorText = string.IsNullOrEmpty(update.ErrorDetail)
                        ? "Telemetry unavailable - the GPU or driver is not responding."
                        : $"Telemetry unavailable ({update.ErrorDetail}).";
                    return;
                }

                var s = update.Snapshot!;
                _latestTempC = s.GpuTempC;
                TelemetryAvailable = true;
                TelemetryErrorText = "";
                CoreClockText = s.CoreClockMHz is null ? "-" : $"{s.CoreClockMHz.Value:0} MHz";
                MemClockText = s.MemClockMHz is null ? "-" : $"{s.MemClockMHz.Value:0} MHz";
                TempText = s.GpuTempC is null ? "-" : $"{s.GpuTempC.Value} °C";
                HotspotText = s.HotspotTempC is null ? "" : $"hotspot {s.HotspotTempC.Value} °C";
                PowerText = FormatPower(s);
                FanText = s.FanPercent is null ? "-"
                    : $"{s.FanPercent.Value}%{(s.FanRpm is null ? "" : $" ({s.FanRpm.Value} RPM)")}";
                UsageText = s.GpuUsagePercent is null ? "-" : $"{s.GpuUsagePercent.Value}%";
                VramText = s.VramUsageMb is null ? "-" : $"{s.VramUsageMb.Value:0} MB";
                PcieText = s.PcieGen is null ? "-" : $"Gen {s.PcieGen.Value} x{s.PcieWidth ?? 0}";
                MemTempText = s.MemTempC is null ? "-" : $"{s.MemTempC.Value} °C";
                VoltageText = s.VoltageMv is null ? "-" : $"{s.VoltageMv.Value:0} mV";
                PcieTputText = s.PcieTxKBs is null || s.PcieRxKBs is null
                    ? "-" : $"TX {FormatRate(s.PcieTxKBs.Value)} · RX {FormatRate(s.PcieRxKBs.Value)}";
                RebuildTelemetryTiles(s);
                PushMonitorSample(s);
            });
        }

        private static string FormatRate(double kbPerSecond)
            => kbPerSecond >= 1048576 ? $"{kbPerSecond / 1048576:0.#} GB/s"
                : kbPerSecond >= 1024 ? $"{kbPerSecond / 1024:0.#} MB/s"
                : $"{kbPerSecond:0} KB/s";

        private void RebuildTelemetryTiles(GpuTelemetrySnapshot s)
        {
            var desired = new List<(string Label, string Value)>(12);
            void Add(string label, string value) => desired.Add((label, value));
            Add("Core clock", CoreClockText);
            Add("Memory clock", MemClockText);
            Add("GPU temperature", TempText);
            if (s.HotspotTempC is { } hs) Add("Hotspot", $"{hs} °C");
            if (s.MemTempC is { } mt) Add("Memory temp", $"{mt} °C");
            Add("GPU voltage", VoltageText);
            Add("Board power", PowerText);
            Add("Fan", FanText);
            Add("GPU usage", UsageText);
            Add("VRAM usage", VramText);
            Add("PCIe bus", PcieText);
            if (s.PcieTxKBs is not null && s.PcieRxKBs is not null) Add("PCIe TX/RX", PcieTputText);

            bool shapeMatches = TelemetryTiles.Count == desired.Count;
            if (shapeMatches)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    if (TelemetryTiles[i].Label != desired[i].Label) { shapeMatches = false; break; }
                }
            }

            if (shapeMatches)
            {
                for (int i = 0; i < desired.Count; i++)
                    TelemetryTiles[i].Value = desired[i].Value;
                return;
            }

            TelemetryTiles.Clear();
            foreach (var (label, value) in desired)
                TelemetryTiles.Add(new TelemetryTileRow(label, value));
        }

        private readonly Queue<MonitorSample> _history = new();
        private const int HistoryLimit = 180;

        public event Action? MonitorUpdated;

        public IReadOnlyList<MonitorSample> GetMonitorHistory() => _history.ToList();

        [ObservableProperty]
        public partial string MonGpuTemp { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonHotspotTemp { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonMemTemp { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonCoreClock { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonMemClock { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonPower { get; private set; } = "-";

        [ObservableProperty]
        public partial string MonFan { get; private set; } = "-";

        private void PushMonitorSample(GpuTelemetrySnapshot s)
        {
            _history.Enqueue(new MonitorSample(
                s.GpuTempC, s.HotspotTempC, s.MemTempC,
                s.CoreClockMHz, s.MemClockMHz, s.PowerDrawW, s.FanPercent));
            while (_history.Count > HistoryLimit) _history.Dequeue();

            MonGpuTemp = s.GpuTempC is null ? "-" : $"{s.GpuTempC}°C";
            MonHotspotTemp = s.HotspotTempC is null ? "-" : $"{s.HotspotTempC}°C";
            MonMemTemp = s.MemTempC is null ? "-" : $"{s.MemTempC}°C";
            MonCoreClock = s.CoreClockMHz is null ? "-" : $"{s.CoreClockMHz.Value:0} MHz";
            MonMemClock = s.MemClockMHz is null ? "-" : $"{s.MemClockMHz.Value:0} MHz";
            MonPower = s.PowerDrawW is null ? "-" : $"{s.PowerDrawW.Value:0.#} W";
            MonFan = s.FanPercent is null ? "-" : $"{s.FanPercent}%";
            MonitorUpdated?.Invoke();
        }

        private static string FormatPower(GpuTelemetrySnapshot s)
        {
            if (s.PowerDrawW is not null) return $"{s.PowerDrawW.Value:0.#} W";
            if (s.PowerDrawPercentOfLimit is not null) return $"{s.PowerDrawPercentOfLimit.Value:0}% of limit";
            return "-";
        }

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
        public partial bool HasXbarOffset { get; private set; }

        [ObservableProperty]
        public partial double XbarOffsetMin { get; private set; }

        [ObservableProperty]
        public partial double XbarOffsetMax { get; private set; }

        [ObservableProperty]
        public partial double XbarOffsetValue { get; private set; }

        [ObservableProperty]
        public partial bool HasSystemOffset { get; private set; }

        [ObservableProperty]
        public partial double SystemOffsetMin { get; private set; }

        [ObservableProperty]
        public partial double SystemOffsetMax { get; private set; }

        [ObservableProperty]
        public partial double SystemOffsetValue { get; private set; }

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

        public string CoreOffsetDisplay => HasCoreOffset ? $"{CoreOffsetValue:+0;-0;0} MHz" : "";
        public string MemOffsetDisplay => HasMemOffset ? $"{MemOffsetValue:+0;-0;0} MHz" : "";
        public string PowerLimitDisplay => HasPowerLimit ? $"{PowerLimitValue:0.#}%" : "";
        public string TempLimitDisplay => HasTempLimit ? $"{TempLimitValue:0} °C" : "";

        partial void OnCoreOffsetValueChanged(double value) => OnPropertyChanged(nameof(CoreOffsetDisplay));
        partial void OnMemOffsetValueChanged(double value) => OnPropertyChanged(nameof(MemOffsetDisplay));
        partial void OnPowerLimitValueChanged(double value) => OnPropertyChanged(nameof(PowerLimitDisplay));
        partial void OnTempLimitValueChanged(double value) => OnPropertyChanged(nameof(TempLimitDisplay));

        private static bool HasAnyControl(AmdGpuTuningController amd)
        {
            var caps = amd.ReadCapabilities();
            return caps is { IsSuccess: true, Value: { AnyControlSupported: true } };
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusText = "Detecting GPU…";
            try
            {
                var init = await Task.Run(_module.Controller.Initialize);
                if (!init.IsSuccess)
                {
                    SetUnsupported(OverclockErrorMessages.For(init.ErrorKind), init.Detail);
                    return;
                }

                var id = await Task.Run(_module.Controller.GetIdentity);
                if (!id.IsSuccess)
                {
                    SetUnsupported(OverclockErrorMessages.For(id.ErrorKind), id.Detail);
                    return;
                }

                if (_module.Controller is AmdGpuTuningController amd && !HasAnyControl(amd))
                {
                    SetUnsupported(
                        $"Found {id.Value?.FullName ?? "this GPU"}. This GPU does not expose any overclocking controls " +
                        "to the driver - integrated adapters share system memory and have no fan to tune. " +
                        "AMD tuning becomes available on a supported discrete Radeon card.");
                    return;
                }

                _module.Telemetry.Start();

                var recovered = await Task.Run(() =>
                    FanCurveExecutionService.EnsureNoOrphanedFanControl(_module.Controller));
                if (recovered)
                {
                    _module.ChangeLog.Log(new AppliedChangeLogEntry
                    {
                        Timestamp = DateTime.Now,
                        ControlName = "Fan mode",
                        OldValue = "forced (previous session ended unexpectedly)",
                        NewValue = "auto (driver default)",
                        Source = OverclockChangeSource.AutoRevert,
                        Result = OverclockChangeResult.Reverted,
                        FailureReason = "app restart after abnormal exit during curve mode",
                    });
                }

                await LoadCapabilitiesAsync();
                RefreshLog();
                _module.GameAutoApply.Start();
                RefreshBindings();
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task RefreshAsync()
        {
            if (FanMode == GpuFanMode.Curve)
            {
                await _module.FanCurve.StopAsync("GPU re-detected - fan restored to auto");
                FanMode = GpuFanMode.Auto;
            }

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

        private void SetUnsupported(string message, string? detail = null)
        {
            IsSupported = false;
            UnsupportedMessage = detail is { Length: > 0 }
                ? $"{message}   [details: {detail}]"
                : message;
            _module.Telemetry.Update -= OnTelemetryUpdate;
        }

        private async Task LoadCapabilitiesAsync()
        {
            var caps = await Task.Run(_module.Controller.ReadCapabilities);
            if (!caps.IsSuccess || caps.Value is null)
            {
                SetUnsupported(OverclockErrorMessages.For(caps.ErrorKind), caps.Detail);
                return;
            }

            var c = caps.Value;

            if (c.IsIntegrated ||
                c.GpuName.Contains("Integrated", StringComparison.OrdinalIgnoreCase) ||
                c.GpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                SetUnsupported("Overclocking is disabled for integrated graphics processors.");
                return;
            }

            _caps = c;
            _gpuName = c.GpuName;
            _driverVersion = c.DriverVersion;
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

            HasXbarOffset = c.XbarOffsetRangeMHz is not null;
            XbarOffsetMin = c.XbarOffsetRangeMHz?.Minimum ?? -500;
            XbarOffsetMax = c.XbarOffsetRangeMHz?.Maximum ?? 500;
            XbarOffsetValue = c.XbarOffsetRangeMHz?.Clamp(0) ?? 0;
            HasSystemOffset = c.SystemOffsetRangeMHz is not null;
            SystemOffsetMin = c.SystemOffsetRangeMHz?.Minimum ?? -500;
            SystemOffsetMax = c.SystemOffsetRangeMHz?.Maximum ?? 500;
            SystemOffsetValue = c.SystemOffsetRangeMHz?.Clamp(0) ?? 0;

            HasPowerLimit = c.PowerLimitRangePercent is not null;
            if (c.PowerLimitRangePercent is { } pr)
            {
                PowerLimitMin = pr.Minimum; PowerLimitMax = pr.Maximum;
                PowerLimitValue = pr.Clamp(powerCur);
            }
            HasTempLimit = c.TempLimitRangeC is not null;
            if (c.TempLimitRangeC is { } tr)
            {
                TempLimitMin = tr.Minimum; TempLimitMax = tr.Maximum;
                TempLimitValue = tr.Clamp(tempCur ?? tr.Maximum);
            }
            IsSupported = c.AnyControlSupported || c.FanControlSupported;
            UnsupportedMessage = IsSupported ? "" : "This GPU exposes no supported overclock controls.";
            FanControlAvailable = c.FanControlSupported;
            ZeroRpmAvailable = c.ZeroRpmSupported;
            if (c.ZeroRpmSupported) _ = SyncZeroRpmAsync();

            FanIntervalMs = _module.FanCurve.Interval.TotalMilliseconds;
            FanRampStep = _module.FanCurve.MaxStepPercentPerTick;

            await LoadVfCurveAsync(c);

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

        [ObservableProperty]
        public partial bool HasVfCurve { get; private set; }

        [ObservableProperty]
        public partial int VfCurvePointCount { get; private set; }

        [ObservableProperty]
        public partial double VfFlatMin { get; private set; }

        [ObservableProperty]
        public partial double VfFlatMax { get; private set; }

        [ObservableProperty]
        public partial double VfFlatValue { get; private set; }

        [ObservableProperty]
        public partial bool VfOffsetsMixed { get; private set; }

        [ObservableProperty]
        public partial string VfCurveError { get; private set; } = "";

        [ObservableProperty]
        public partial bool HasVoltageBoost { get; private set; }

        [ObservableProperty]
        public partial double VoltageBoostPercentValue { get; private set; }

        public ObservableCollection<VfCurvePointRow> VfPoints { get; } = new();

        public string VfFlatDisplay => HasVfCurve ? $"{VfFlatValue:+0;-0;0} MHz" : "";
        public string VoltageBoostPercentDisplay => HasVoltageBoost ? $"{VoltageBoostPercentValue:0}%" : "";

        partial void OnVfFlatValueChanged(double value) => OnPropertyChanged(nameof(VfFlatDisplay));
        partial void OnVoltageBoostPercentValueChanged(double value) => OnPropertyChanged(nameof(VoltageBoostPercentDisplay));

        private GpuVoltageFrequencyCurve? _liveCurve;

        private GpuCapabilities? _caps;

        private bool _vfAdvancedDirty;

        private async Task LoadVfCurveAsync(GpuCapabilities caps)
        {
            HasVfCurve = false;
            VfPoints.Clear();
            _liveCurve = null;
            _vfAdvancedDirty = false;
            VfCurveError = "";
            HasVoltageBoost = false;
            if (!caps.VfCurveSupported) return;

            var res = await Task.Run(_module.Controller.ReadVoltageFrequencyCurve);
            if (!res.IsSuccess || res.Value is null || res.Value.Count == 0) return;

            var curve = res.Value;
            _liveCurve = curve;
            foreach (var p in curve.Points) VfPoints.Add(new VfCurvePointRow(p));
            if (curve.WidestOffsetRange() is { } span)
            {
                VfFlatMin = span.Min;
                VfFlatMax = span.Max;
            }
            SyncVfSimpleFromPoints();
            HasVoltageBoost = curve.VoltageBoostSupported;
            if (curve.VoltageBoostSupported) VoltageBoostPercentValue = curve.CurrentVoltageBoostPercent;
            VfCurvePointCount = curve.Count;
            HasVfCurve = true;
            OnPropertyChanged(nameof(VfFlatDisplay));
        }

        private void SyncVfSimpleFromPoints()
        {
            if (VfPoints.Count == 0) return;
            int first = VfPoints[0].OffsetMHz;
            bool uniform = true;
            foreach (var r in VfPoints)
                if (r.OffsetMHz != first) { uniform = false; break; }
            VfOffsetsMixed = !uniform;
            VfFlatValue = uniform ? first : 0;
        }

        public void OnVfFlatCommitted(double value)
        {
            if (_liveCurve is null || !HasVfCurve) return;
            VfFlatValue = value;
            var offsets = _liveCurve.ExpandFlatOffset((int)value);
            if (!CheckVfMonotonic(offsets)) return;
            for (int i = 0; i < VfPoints.Count && i < offsets.Count; i++)
                VfPoints[i].OffsetMHz = offsets[i];
            VfOffsetsMixed = false;
            _vfAdvancedDirty = false;
            QueueVfCurveWrite(offsets, $"flat {(int)value:+0;-0;0} MHz");
        }

        public void ApplyVfCurveEdits()
        {
            if (_liveCurve is null || !HasVfCurve || VfPoints.Count == 0) return;
            var offsets = new List<int>(VfPoints.Count);
            foreach (var r in VfPoints) offsets.Add(r.OffsetMHz);
            if (!CheckVfMonotonic(offsets)) return;
            _vfAdvancedDirty = true;
            SyncVfSimpleFromPoints();
            QueueVfCurveWrite(offsets, "per-point edits");
        }

        public void ClearVfCurveError() => VfCurveError = "";

        public void ResetVfCurve()
        {
            if (_liveCurve is null || !HasVfCurve) return;
            var zeros = new int[VfPoints.Count];
            VfCurveError = "";
            for (int i = 0; i < VfPoints.Count; i++) VfPoints[i].OffsetMHz = 0;
            VfOffsetsMixed = false;
            VfFlatValue = 0;
            _vfAdvancedDirty = false;
            QueueVfCurveWrite(zeros, "stock curve");
        }

        private bool CheckVfMonotonic(IReadOnlyList<int> offsets)
        {
            if (_liveCurve!.ValidateMonotonic(offsets, out string? error))
            {
                VfCurveError = "";
                return true;
            }
            VfCurveError = error ?? "Curve invalid.";
            return false;
        }

        private void QueueVfCurveWrite(IReadOnlyList<int> offsets, string newDisplay)
        {
            var snapshot = new int[offsets.Count];
            for (int i = 0; i < snapshot.Length; i++) snapshot[i] = offsets[i];
            QueueWrite(OcControlNames.VoltageFrequencyCurve,
                () => _module.Controller.SetVoltageFrequencyCurveOffsets(snapshot),
                "current curve", $"{newDisplay} ({snapshot.Length} pts)");
        }

        public void OnVoltageBoostPercentCommitted(double value)
        {
            if (!HasVoltageBoost) return;
            VoltageBoostPercentValue = value;
            QueueWrite(OcControlNames.VoltageBoost,
                () => _module.Controller.SetVoltageBoostPercent((uint)value),
                "previous %", $"{(uint)value}%");
        }

        private async Task ResyncVfFromHardwareAsync()
        {
            if (!HasVfCurve || _liveCurve is null) return;
            var vf = await Task.Run(_module.Controller.ReadVfCurveOffsets);
            if (vf.IsSuccess && vf.Value is { Length: > 0 } arr && arr.Length == VfPoints.Count)
            {
                for (int i = 0; i < arr.Length; i++) VfPoints[i].OffsetMHz = arr[i];
                SyncVfSimpleFromPoints();
            }
        }

        public void OnCoreOffsetCommitted(double value)
        {
            CoreOffsetValue = value;
            QueueWrite(OcControlNames.CoreClockOffset, () => _module.Controller.SetCoreOffsetMhz((int)value),
                $"{_anchors.CoreMhz:+0;-0;0} MHz", $"{(int)value:+0;-0;0} MHz");
        }

        public void OnXbarOffsetCommitted(double value)
        {
            if (!HasXbarOffset) return;
            XbarOffsetValue = value;
            QueueWrite(OcControlNames.XbarClockOffset, () => _module.Controller.SetXbarOffsetMhz((int)value),
                "driver value", $"{(int)value:+0;-0;0} MHz");
        }

        public void OnSystemOffsetCommitted(double value)
        {
            if (!HasSystemOffset) return;
            SystemOffsetValue = value;
            QueueWrite(OcControlNames.SystemClockOffset, () => _module.Controller.SetSystemOffsetMhz((int)value),
                "driver value", $"{(int)value:+0;-0;0} MHz");
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

        private void QueueWrite(string controlName, Func<GpuResult> apply, string oldDisplay, string newDisplay)
        {
            if (!IsSupported) return;
            _dirty[controlName] = new PendingWrite(controlName, apply, oldDisplay, newDisplay);
            _debounceTimer ??= _dispatcher.CreateTimer();

            _debounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _debounceTimer.IsRepeating = false;
            _debounceTimer.Tick -= FlushDirty;
            _debounceTimer.Tick += FlushDirty;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void FlushDirty(object? sender, object e)
        {
            _debounceTimer?.Stop();
            if (_dirty.Count == 0) return;

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
            await ResyncVfFromHardwareAsync();
        }

        private bool _windowWasReverted;
        private DateTime _lastRevertUtc = DateTime.MinValue;

        private bool _lastRevertWasRecent => DateTime.UtcNow - _lastRevertUtc < TimeSpan.FromSeconds(30);

        private async Task<bool> WaitForSafetyIdleAsync(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (Safety.CurrentState == SafetyState.Idle) return true;
                await Task.Delay(100);
            }
            return Safety.CurrentState == SafetyState.Idle;
        }

        private void OnSafetyStateChanged(object? sender, SafetyStateChangedEventArgs e)
        {
            if (e.State == SafetyState.Idle && !string.IsNullOrEmpty(e.RevertReason))
                _lastRevertUtc = DateTime.UtcNow;

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

                    if (e.RevertReason == "driver reset detected" && ZeroRpmEnabled)
                    {
                        ZeroRpmEnabled = false;
                        _ = Task.Run(() => _module.Controller.SetZeroRpmEnabled(false))
                            .ContinueWith(t =>
                            {
                                if (!t.Result.IsSuccess) return;
                                _dispatcher.TryEnqueue(() => StatusText += " Zero RPM was turned off.");
                            });
                    }

                    _ = ResyncControlsFromHardwareAsync();
                }
                else if (e.State == SafetyState.AwaitingConfirmation)
                {
                    StatusText = "";
                }
                RefreshLog();
            });
        }

        [ObservableProperty]
        public partial bool RiskAccepted { get; private set; }

        [RelayCommand]
        public void AcceptRisk() => RiskAccepted = true;

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

        [ObservableProperty]
        public partial GpuFanMode FanMode { get; private set; } = GpuFanMode.Auto;

        [ObservableProperty]
        public partial double FanStaticPercent { get; set; } = 50;

        [ObservableProperty]
        public partial double FanIntervalMs { get; set; } = 2000;

        [ObservableProperty]
        public partial double FanRampStep { get; set; } = 0;

        partial void OnFanIntervalMsChanged(double value)
        {
            double clamped = Math.Clamp(value, 100, 10000);
            if (clamped != value) { FanIntervalMs = clamped; return; }
            _module.FanCurve.Interval = TimeSpan.FromMilliseconds(clamped);
        }

        partial void OnFanRampStepChanged(double value)
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (clamped != value) { FanRampStep = clamped; return; }
            _module.FanCurve.MaxStepPercentPerTick = (int)clamped;
        }

        [ObservableProperty]
        public partial bool FanControlAvailable { get; private set; }

        [ObservableProperty]
        public partial bool ZeroRpmAvailable { get; private set; }

        [ObservableProperty]
        public partial bool ZeroRpmEnabled { get; private set; }

        public async Task SetZeroRpmAsync(bool enabled)
        {
            if (Safety.CurrentState != SafetyState.Idle) return;

            if (enabled && FanMode != GpuFanMode.Auto)
            {
                StatusText = "Set the fan to Auto before enabling Zero RPM - the two fight each other.";
                ZeroRpmEnabled = false;
                return;
            }

            var result = await Task.Run(() => _module.Controller.SetZeroRpmEnabled(enabled));
            if (!result.IsSuccess)
            {
                StatusText = OverclockErrorMessages.For(result.ErrorKind);
                ZeroRpmEnabled = false;
                return;
            }

            ZeroRpmEnabled = enabled;
            RefreshLog();
        }

        public async Task SyncZeroRpmAsync()
        {
            var read = await Task.Run(() => _module.Controller.IsZeroRpmEnabled());
            ZeroRpmEnabled = read.IsSuccess && read.Value;
        }

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
            if (FanMode == GpuFanMode.Static)
                QueueWrite(OcControlNames.FanSpeed, () => _module.Controller.SetFanStaticPercent((int)value),
                    "previous %", $"{(int)value}%");
        }

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
            OnPropertyChanged(nameof(StartupDefaultName));
            RefreshBindings();
        }

        public string? StartupDefaultName => _module.Profiles.GetDefaultProfileName();

        [RelayCommand]
        public void SetStartupDefault(OverclockProfile? profile)
        {
            if (profile is null) return;
            if (_module.Profiles.GetDefaultProfileName() == profile.Name)
            {
                _module.Profiles.ClearDefaultProfile();
            }
            else
            {
                _module.Profiles.SetDefaultProfile(profile.Name);
            }
            RefreshProfiles();
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
            if (HasVfCurve && _liveCurve is not null)
            {
                if (_vfAdvancedDirty)
                {
                    p.VfCurveOffsets = _liveCurve.ClampOffsets(VfPoints.Select(r => r.OffsetMHz).ToList());
                }
                else if ((int)VfFlatValue != 0)
                {
                    p.GlobalVoltageBoostOffsetMHz = (int)VfFlatValue;
                }
            }
            _module.Profiles.Save(p);
            NewProfileName = "";
            RefreshProfiles();
        }

        [RelayCommand]
        public async Task ApplyProfileAsync(OverclockProfile? profile)
        {
            if (profile is null || Safety.CurrentState != SafetyState.Idle) return;
            if (_caps is null)
            {
                StatusText = "The GPU hasn't been detected yet - try again in a moment.";
                return;
            }

            string OldText(string control) => control switch
            {
                OcControlNames.CoreClockOffset => $"{_anchors.CoreMhz:+0;-0;0} MHz",
                OcControlNames.MemoryClockOffset => $"{_anchors.MemMhz:+0;-0;0} MHz",
                OcControlNames.PowerLimit => $"{_anchors.PowerPct:0.#}%",
                OcControlNames.TemperatureLimit => _anchors.TempC is null ? "driver default" : $"{_anchors.TempC} °C",
                OcControlNames.VoltageFrequencyCurve => "current curve",
                _ => "current",
            };
            var batch = ProfileBatchBuilder.Build(
                _module.Controller, _caps, profile, OldText, _liveCurve, out string? curveSkipped);
            if (curveSkipped is not null) StatusText = curveSkipped;

            _windowWasReverted = false;
            if (batch.Count > 0)
            {
                var ok = await Task.Run(() => _module.Safety.ApplyBatchAsync(batch, OverclockChangeSource.ProfileApply));
                if (!ok)
                {
                    StatusText = "The driver refused one or more values from this profile.";
                    return;
                }
                int window = _module.Safety.GetConfirmationSecondsFor(batch.Select(b => b.ControlName));
                var closedCleanly = await WaitForSafetyIdleAsync(TimeSpan.FromSeconds(window + 5));
                _windowWasReverted = !closedCleanly || _lastRevertWasRecent;
            }

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
            if (Safety.CurrentState == SafetyState.Idle && !_windowWasReverted)
            {
                profile.ConfirmedAt = DateTime.Now;
                profile.HasBeenManuallyValidated = true;
                profile.LastValidatedAt = DateTime.Now;
            }
            _module.Profiles.Save(profile);
            await ResyncVfFromHardwareAsync();
            RefreshProfiles();
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
                if (_module.Profiles.GetDefaultProfileName() is null)
                {
                    StatusText = "Designate a profile as the startup default first (toggle \"At startup\" on a profile).";
                    StartupReapplyEnabled = false;
                    return;
                }
                _module.StartupTask.Register();
            }
            StartupReapplyEnabled = _module.StartupTask.IsRegistered;
        }

        public ObservableCollection<GameBindingRow> Bindings { get; } = new();

        [ObservableProperty]
        public partial string ActiveGameProfileText { get; private set; } = "No game profile active.";

        [ObservableProperty]
        public partial string LastAutoSwitchText { get; private set; } = "";

        [ObservableProperty]
        public partial string GameWatcherStatusText { get; private set; } = "";

        private void OnAutoApplyStatus(GameProfileAutoApplyService.AutoApplyStatus status)
        {
            _dispatcher.TryEnqueue(() =>
            {
                ActiveGameProfileText = string.IsNullOrWhiteSpace(status.ActiveProfileName)
                    ? "No game profile active."
                    : $"Currently active: {status.ActiveProfileName}" +
                      (string.IsNullOrWhiteSpace(status.ActiveExecutable) ? "" : $" (for {status.ActiveExecutable})");
                LastAutoSwitchText = string.IsNullOrWhiteSpace(status.LastMessage)
                    ? "" : $"{status.LastMessageAt:HH:mm:ss} - {status.LastMessage}";
                GameWatcherStatusText = $"Watcher: {status.WatcherMode}";
            });
        }

        [RelayCommand]
        public void RefreshBindings()
        {
            Bindings.Clear();
            var byId = new Dictionary<Guid, OverclockProfile>();
            foreach (var p in _module.Profiles.LoadAll()) byId[p.Id] = p;
            foreach (var b in _module.Profiles.LoadBindings())
            {
                byId.TryGetValue(b.ProfileId, out var prof);
                Bindings.Add(new GameBindingRow(
                    b, prof?.Name ?? "(deleted profile)",
                    prof?.HasBeenManuallyValidated == true));
            }
            _module.GameAutoApply.RefreshBindings();
            GameWatcherStatusText = $"Watcher: {_module.GameWatcher.ModeDescription}";
        }

        [RelayCommand]
        public async Task BindProfileToGameAsync(OverclockProfile? profile)
        {
            if (profile is null) return;
            string? path = await PickGameExecutableAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            var binding = new GameProfileBinding
            {
                ExecutableName = System.IO.Path.GetFileName(path) ?? path.Trim(),
                ProfileId = profile.Id,
                Enabled = true,
            };
            _module.Profiles.SaveBinding(binding);
            RefreshBindings();
            StatusText = profile.HasBeenManuallyValidated
                ? $"Bound '{profile.Name}' to {binding.NormalizedExecutableName} - auto-switch is live."
                : $"Bound '{profile.Name}' to {binding.NormalizedExecutableName} - apply it manually once to enable auto-switch.";
        }

        [RelayCommand]
        public void RemoveBinding(GameBindingRow? row)
        {
            if (row is null) return;
            _module.Profiles.DeleteBinding(row.Binding.Id);
            RefreshBindings();
        }

        public void SetBindingEnabled(GameBindingRow row, bool enabled)
        {
            row.Binding.Enabled = enabled;
            row.Enabled = enabled;
            _module.Profiles.SaveBinding(row.Binding);
            RefreshBindings();
        }

        [RelayCommand]
        public void ClearBindingPath(GameBindingRow? row)
        {
            if (row is null) return;
            row.Binding.FullPath = null;
            _module.Profiles.SaveBinding(row.Binding);
            RefreshBindings();
        }

        [RelayCommand]
        public async Task LockBindingPathAsync(GameBindingRow? row)
        {
            if (row is null) return;
            string? path = await PickGameExecutableAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            row.Binding.ExecutableName = System.IO.Path.GetFileName(path) ?? path.Trim();
            row.Binding.FullPath = path;
            _module.Profiles.SaveBinding(row.Binding);
            RefreshBindings();
        }

        private async Task<string?> PickGameExecutableAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add(".exe");
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                var file = await picker.PickSingleFileAsync();
                if (file is not null) return file.Path;
            }
            catch
            {
            }
            try
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : 0;
                return kaliteConfig.Services.Win32FilePicker.PickOpenFile(
                    hwnd,
                    new[] { ("Application (*.exe)", "*.exe"), ("All files (*.*)", "*.*") },
                    "Select the game executable to bind");
            }
            catch
            {
                return null;
            }
        }

        [ObservableProperty]
        public partial int LogResultFilterIndex { get; set; }

        [ObservableProperty]
        public partial int LogSourceFilterIndex { get; set; }

        public bool LogIsEmpty => LogEntries.Count == 0;

        partial void OnLogResultFilterIndexChanged(int value) => RefreshLog();
        partial void OnLogSourceFilterIndexChanged(int value) => RefreshLog();

        [RelayCommand]
        public void RefreshLog()
        {
            var resultFilter = OverclockLogFilter.ResultFromIndex(LogResultFilterIndex);
            var sourceFilter = OverclockLogFilter.SourceFromIndex(LogSourceFilterIndex);
            LogEntries.Clear();
            foreach (var e in _module.ChangeLog.Tail().Reverse())
                if (OverclockLogFilter.Matches(e, resultFilter, sourceFilter))
                    LogEntries.Add(e);
            OnPropertyChanged(nameof(LogIsEmpty));
        }

        private string? _gpuName;
        private string? _driverVersion;

        [RelayCommand]
        public async Task ExportVerificationRecordAsync()
        {
            var markdown = OverclockVerificationExporter.BuildMarkdown(
                _module.ChangeLog.Tail(), _gpuName, _driverVersion, DateTime.Now);
            var suggested = OverclockVerificationExporter.SuggestedFileName(DateTime.Now);

            string? path = null;
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
                picker.SuggestedFileName = suggested;
                path = (await picker.PickSaveFileAsync())?.Path;
            }
            catch
            {
                var hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : 0;
                path = kaliteConfig.Services.Win32FilePicker.PickSaveFile(
                    hwnd, suggested,
                    new[] { ("Markdown record (*.md)", "*.md") },
                    "Export overclock verification record", ".md");
            }
            if (path is null) return;

            try
            {
                await Task.Run(() => File.WriteAllText(path, markdown));
                StatusText = $"Verification record exported: {path}";
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }
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
            catch {  }
        }

        public void Teardown()
        {
            _module.Telemetry.Update -= OnTelemetryUpdate;
            _module.Safety.StateChanged -= OnSafetyStateChanged;
            _module.FanCurve.Stopped -= OnFanCurveStopped;
            _module.GameAutoApply.StatusChanged -= OnAutoApplyStatus;
            _debounceTimer?.Stop();

            _ = _module.Telemetry.StopAsync();
        }
    }

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

    public sealed class VfCurvePointRow : ObservableObject
    {
        private int _offsetMHz;

        public VfCurvePointRow(VfCurvePoint p)
        {
            VoltageMv = p.VoltageMv;
            BaseFrequencyMHz = p.BaseFrequencyMHz;
            MinOffsetMHz = p.MinOffsetMHz;
            MaxOffsetMHz = p.MaxOffsetMHz;
            _offsetMHz = p.OffsetMHz;
        }

        public int VoltageMv { get; }
        public int BaseFrequencyMHz { get; }
        public int MinOffsetMHz { get; }
        public int MaxOffsetMHz { get; }

        public int OffsetMHz
        {
            get => _offsetMHz;
            set
            {
                int lo = Math.Min(MinOffsetMHz, MaxOffsetMHz);
                int hi = Math.Max(MinOffsetMHz, MaxOffsetMHz);
                int clamped = Math.Clamp(value, lo, hi);
                if (SetProperty(ref _offsetMHz, clamped))
                    OnPropertyChanged(nameof(EffectiveMHz));
            }
        }

        public int EffectiveMHz => BaseFrequencyMHz + OffsetMHz;

        public string Label => $"{VoltageMv} mV · {EffectiveMHz} MHz ({OffsetMHz:+0;-0;0})";
    }

    public sealed class GameBindingRow : ObservableObject
    {
        public GameBindingRow(GameProfileBinding binding, string profileName, bool profileValidated)
        {
            Binding = binding;
            ProfileName = profileName;
            ProfileValidated = profileValidated;
            _enabled = binding.Enabled;
        }

        public GameProfileBinding Binding { get; }
        public string ProfileName { get; }
        public bool ProfileValidated { get; }

        public bool ProfileMissing => ProfileName == "(deleted profile)";
        public string ExeDisplay => Binding.NormalizedExecutableName;
        public bool IsPathLocked => !string.IsNullOrWhiteSpace(Binding.FullPath);

        public string PathDisplay => IsPathLocked
            ? Binding.FullPath!
            : "executable-name match (portable across install locations)";

        public string ValidationDisplay => ProfileMissing
            ? "profile deleted - re-bind or remove"
            : ProfileValidated
                ? "auto-switch ready ✓"
                : "not yet validated - apply manually once to enable auto-switch";

        private bool _enabled;

        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }
    }

    public sealed class TelemetryTileRow : ObservableObject
    {
        private string _value = "-";

        public TelemetryTileRow(string label, string value)
        {
            Label = label;
            _value = value;
        }

        public string Label { get; }

        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }

    public sealed record MonitorSample(
        int? GpuTempC,
        int? HotspotTempC,
        int? MemTempC,
        double? CoreClockMHz,
        double? MemClockMHz,
        double? PowerDrawW,
        int? FanPercent);
}
