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
            _module.GameAutoApply.StatusChanged += OnAutoApplyStatus;

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

        [ObservableProperty]
        public partial string MemTempText { get; private set; } = "—";

        [ObservableProperty]
        public partial string VoltageText { get; private set; } = "—";

        [ObservableProperty]
        public partial string PcieTputText { get; private set; } = "—";

        /// <summary>Tile rows for the responsive telemetry grid (rebuilt every tick).</summary>
        public ObservableCollection<TelemetryTileRow> TelemetryTiles { get; } = new();

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
                MemTempText = s.MemTempC is null ? "—" : $"{s.MemTempC.Value} °C";
                VoltageText = s.VoltageMv is null ? "—" : $"{s.VoltageMv.Value:0} mV";
                PcieTputText = s.PcieTxKBs is null || s.PcieRxKBs is null
                    ? "—" : $"TX {FormatRate(s.PcieTxKBs.Value)} · RX {FormatRate(s.PcieRxKBs.Value)}";
                RebuildTelemetryTiles(s);
                PushMonitorSample(s);
            });
        }

        private static string FormatRate(double kbPerSecond)
            => kbPerSecond >= 1048576 ? $"{kbPerSecond / 1048576:0.#} GB/s"
                : kbPerSecond >= 1024 ? $"{kbPerSecond / 1024:0.#} MB/s"
                : $"{kbPerSecond:0} KB/s";

        /// <summary>
        /// Rebuilds the responsive tile grid in the brief's two-row order.
        /// Conditional tiles (hotspot, memory temp, PCIe throughput) are
        /// omitted when the GPU doesn't report them — never shown as zeros.
        /// </summary>
        private void RebuildTelemetryTiles(GpuTelemetrySnapshot s)
        {
            TelemetryTiles.Clear();
            void Add(string label, string value) => TelemetryTiles.Add(new TelemetryTileRow(label, value));
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
        }

        // ---------------- monitor history (live graphs incl. hotspot) ----------------

        private readonly Queue<MonitorSample> _history = new();
        private const int HistoryLimit = 180; // 1s ticks → ~3 minutes of history

        /// <summary>Raised on the UI thread after each tick appends history.</summary>
        public event Action? MonitorUpdated;

        /// <summary>Copy of the history for the graph canvases (UI thread only).</summary>
        public IReadOnlyList<MonitorSample> GetMonitorHistory() => _history.ToList();

        [ObservableProperty]
        public partial string MonGpuTemp { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonHotspotTemp { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonMemTemp { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonCoreClock { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonMemClock { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonPower { get; private set; } = "—";

        [ObservableProperty]
        public partial string MonFan { get; private set; } = "—";

        private void PushMonitorSample(GpuTelemetrySnapshot s)
        {
            _history.Enqueue(new MonitorSample(
                s.GpuTempC, s.HotspotTempC, s.MemTempC,
                s.CoreClockMHz, s.MemClockMHz, s.PowerDrawW, s.FanPercent));
            while (_history.Count > HistoryLimit) _history.Dequeue();

            MonGpuTemp = s.GpuTempC is null ? "—" : $"{s.GpuTempC}°C";
            MonHotspotTemp = s.HotspotTempC is null ? "—" : $"{s.HotspotTempC}°C";
            MonMemTemp = s.MemTempC is null ? "—" : $"{s.MemTempC}°C";
            MonCoreClock = s.CoreClockMHz is null ? "—" : $"{s.CoreClockMHz.Value:0} MHz";
            MonMemClock = s.MemClockMHz is null ? "—" : $"{s.MemClockMHz.Value:0} MHz";
            MonPower = s.PowerDrawW is null ? "—" : $"{s.PowerDrawW.Value:0.#} W";
            MonFan = s.FanPercent is null ? "—" : $"{s.FanPercent}%";
            MonitorUpdated?.Invoke();
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

                // Crash-orphan recovery: if a previous process died mid-Curve
                // mode, its forced fan % would persist silently. The liveness
                // marker detects that and hands the fan back to the driver
                // before the user ever sees a stuck fan.
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
                // Phase 6: populate the viewer immediately — startup-path entries
                // (orphan recovery, startup reapply) are logged before this.
                RefreshLog();
                // v2 Part B: the game watcher lives as long as the app — start
                // it here (idempotent) so bindings work even if the user never
                // opens the profiles expander.
                _module.GameAutoApply.Start();
                RefreshBindings();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Called on GPU re-detect: stops the fan curve loop (spec §5 — the loop
        /// must hand back to Auto when the GPU is re-detected; after a re-detect
        /// the controller may resolve a different adapter, and a blind loop
        /// would force fan speeds on it), forgets the cached handle and re-reads
        /// ranges.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (FanMode == GpuFanMode.Curve)
            {
                await _module.FanCurve.StopAsync("GPU re-detected — fan restored to auto");
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

            // Pick up a persisted-by-session fan loop setup (new VM, same process).
            FanIntervalMs = _module.FanCurve.Interval.TotalMilliseconds;
            FanRampStep = _module.FanCurve.MaxStepPercentPerTick;

            await LoadVfCurveAsync(c);

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

        // ---------------- V/F curve + voltage boost (v2) ----------------

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

        /// <summary>True when the live per-point offsets are not all equal (simple slider can't represent them).</summary>
        [ObservableProperty]
        public partial bool VfOffsetsMixed { get; private set; }

        /// <summary>Client-side validation error (e.g. non-monotonic) — blocks commit while set.</summary>
        [ObservableProperty]
        public partial string VfCurveError { get; private set; } = "";

        /// <summary>
        /// True only when the driver actually answers the voltage-boost-percent
        /// query. Usually false on Ampere/Ada (vBIOS-locked) — the control
        /// stays hidden then and nothing implies true overvolting.
        /// </summary>
        [ObservableProperty]
        public partial bool HasVoltageBoost { get; private set; }

        [ObservableProperty]
        public partial double VoltageBoostPercentValue { get; private set; }

        public ObservableCollection<VfCurvePointRow> VfPoints { get; } = new();

        public string VfFlatDisplay => HasVfCurve ? $"{VfFlatValue:+0;-0;0} MHz" : "";
        public string VoltageBoostPercentDisplay => HasVoltageBoost ? $"{VoltageBoostPercentValue:0}%" : "";

        partial void OnVfFlatValueChanged(double value) => OnPropertyChanged(nameof(VfFlatDisplay));
        partial void OnVoltageBoostPercentValueChanged(double value) => OnPropertyChanged(nameof(VoltageBoostPercentDisplay));

        /// <summary>
        /// Session-start base anchor: queried once, never re-derived, so the
        /// base labels can't drift after applies (see controller notes).
        /// </summary>
        private GpuVoltageFrequencyCurve? _liveCurve;

        private GpuCapabilities? _caps;

        /// <summary>True once the user drags in the advanced editor (saved as a per-point table, not flat).</summary>
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

        private void RefreshVfRowsFromLive()
        {
            if (_liveCurve is null) return;
            VfPoints.Clear();
            foreach (var p in _liveCurve.Points) VfPoints.Add(new VfCurvePointRow(p));
            SyncVfSimpleFromPoints();
        }

        /// <summary>Simple mode: one flat offset across all points (UI shorthand — writes the full table).</summary>
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

        /// <summary>Advanced mode: commit the per-point edits from the graph/table.</summary>
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

        /// <summary>Reset to stock curve: zero deltas, still routed through the safety machine.</summary>
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

        /// <summary>Re-reads live curve offsets into the rows (base anchor untouched).</summary>
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

        /// <summary>Curve-loop tick interval in ms (live on the service; session-scoped, not saved to profiles).</summary>
        [ObservableProperty]
        public partial double FanIntervalMs { get; set; } = 2000;

        /// <summary>Ramp cap in percentage-points per tick; 0 = off (direct apply, as before).</summary>
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
            OnPropertyChanged(nameof(StartupDefaultName));
            RefreshBindings();
        }

        /// <summary>Designated startup profile (or null) — shown by the UI.</summary>
        public string? StartupDefaultName => _module.Profiles.GetDefaultProfileName();

        /// <summary>
        /// Designates/clears the startup default. Registration and the known-safe
        /// flag are separate concerns: the task runs the app with the reapply
        /// argument; only pre-boot-validated profiles are actually applied.
        /// </summary>
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
            OnPropertyChanged(nameof(StartupDefaultName));
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
                    // Advanced edits: persist the per-point table (clamped to
                    // live ranges so a hand-edited file can't smuggle garbage).
                    p.VfCurveOffsets = _liveCurve.ClampOffsets(VfPoints.Select(r => r.OffsetMHz).ToList());
                }
                else if ((int)VfFlatValue != 0)
                {
                    // Simple mode: persist the portable flat value; the apply
                    // path expands it against whatever GPU is live then.
                    p.GlobalVoltageBoostOffsetMHz = (int)VfFlatValue;
                }
            }
            _module.Profiles.Save(p);
            NewProfileName = "";
            RefreshProfiles();
        }

        /// <summary>
        /// Applies a profile as one batch through the same safety state machine —
        /// profile loads are never exempt (spec 6). Uses the shared
        /// ProfileBatchBuilder so interactive, startup, and game auto-switch
        /// applies are byte-identical.
        /// </summary>
        [RelayCommand]
        public async Task ApplyProfileAsync(OverclockProfile? profile)
        {
            if (profile is null || Safety.CurrentState != SafetyState.Idle) return;
            if (_caps is null)
            {
                StatusText = "The GPU hasn't been detected yet — try again in a moment.";
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
                // Give the confirmation window a moment: a revert inside it must
                // NOT mark the profile pre-boot validated. Curve batches run a
                // longer window — wait for the batch's actual window.
                int window = _module.Safety.GetConfirmationSecondsFor(batch.Select(b => b.ControlName));
                var closedCleanly = await WaitForSafetyIdleAsync(TimeSpan.FromSeconds(window + 5));
                _windowWasReverted = !closedCleanly || _lastRevertWasRecent;
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
            // A profile whose interactive batch survived its confirmation window
            // becomes "pre-boot validated" — eligible for startup reapply.
            // The same clean interactive confirm ALSO marks it manually
            // validated — eligible for per-game auto-switch. Only this
            // desktop interactive path sets the manual flag: startup reapply
            // and game auto-apply never do.
            if (Safety.CurrentState == SafetyState.Idle && !_windowWasReverted)
            {
                profile.ConfirmedAt = DateTime.Now;
                profile.HasBeenManuallyValidated = true;
                profile.LastValidatedAt = DateTime.Now;
            }
            _module.Profiles.Save(profile);
            await ResyncVfFromHardwareAsync();
            RefreshProfiles(); // validation badges + binding validity may have changed
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
                // Registering the task without a designated profile would run
                // the reapply argument against nothing — require the default
                // first so registration state never silently diverges from
                // what the startup path will actually do.
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

        // ---------------- per-game auto-switch (v2 Part B) ----------------

        public ObservableCollection<GameBindingRow> Bindings { get; } = new();

        /// <summary>Persistent "currently active" indicator (also mirrored to the tray tooltip).</summary>
        [ObservableProperty]
        public partial string ActiveGameProfileText { get; private set; } = "No game profile active.";

        /// <summary>Last auto-switch event, timestamped — visible next time the app opens.</summary>
        [ObservableProperty]
        public partial string LastAutoSwitchText { get; private set; } = "";

        /// <summary>Watcher mechanism (event-driven WMI vs polling fallback) — never silently degraded.</summary>
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
                    ? "" : $"{status.LastMessageAt:HH:mm:ss} — {status.LastMessage}";
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

        /// <summary>
        /// Binds a profile to a game executable (file picker). Portable by
        /// default (executable-name match) — the row offers exact-path
        /// locking for generically-named executables.
        /// </summary>
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
                ? $"Bound '{profile.Name}' to {binding.NormalizedExecutableName} — auto-switch is live."
                : $"Bound '{profile.Name}' to {binding.NormalizedExecutableName} — apply it manually once to enable auto-switch.";
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

        /// <summary>Relaxes a binding to a portable executable-name match.</summary>
        [RelayCommand]
        public void ClearBindingPath(GameBindingRow? row)
        {
            if (row is null) return;
            row.Binding.FullPath = null;
            _module.Profiles.SaveBinding(row.Binding);
            RefreshBindings();
        }

        /// <summary>Locks a binding to an exact executable path (file picker).</summary>
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

        /// <summary>
        /// Game-executable picker. The brokered WinUI picker throws in
        /// elevated processes, so the Win32 common dialog is the fallback —
        /// the same pattern the verification-record export uses.
        /// </summary>
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
                // Elevated session → brokered picker unavailable; Win32 dialog works.
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

        // ---------------- change log ----------------

        /// <summary>ComboBox index of the result filter: 0 = all results,
        /// 1..n follow the OverclockChangeResult declaration order — mapped in
        /// OverclockLogFilter so the mapping itself stays unit-testable.</summary>
        [ObservableProperty]
        public partial int LogResultFilterIndex { get; set; }

        /// <summary>ComboBox index of the source filter: 0 = all sources,
        /// 1..n follow the OverclockChangeSource declaration order — mapped in
        /// OverclockLogFilter.</summary>
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

        // ---------------- verification record export ----------------

        private string? _gpuName;
        private string? _driverVersion;

        /// <summary>
        /// Exports the in-memory change-log tail as a Markdown verification
        /// record (same shape as Docs/OverclockVerification.md). The brokered
        /// WinUI save picker throws in elevated processes, so the Win32 common
        /// dialog is the fallback — the same pattern BiosManager uses.
        /// </summary>
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
                // Elevated session → brokered picker unavailable; the Win32 dialog works.
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
            catch { /* log file may not exist yet */ }
        }

        public void Teardown()
        {
            _module.Telemetry.Update -= OnTelemetryUpdate;
            _module.Safety.StateChanged -= OnSafetyStateChanged;
            _module.FanCurve.Stopped -= OnFanCurveStopped;
            _module.GameAutoApply.StatusChanged -= OnAutoApplyStatus;
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

    /// <summary>
    /// Editable row for one V/F curve point. Voltage and base frequency are
    /// read-only driver values; only the offset is user-editable (clamped to
    /// the point's driver-queried range by the canvas/editor before commit).
    /// </summary>
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
                int clamped = Math.Clamp(value, MinOffsetMHz, MaxOffsetMHz);
                if (SetProperty(ref _offsetMHz, clamped))
                    OnPropertyChanged(nameof(EffectiveMHz));
            }
        }

        public int EffectiveMHz => BaseFrequencyMHz + OffsetMHz;

        public string Label => $"{VoltageMv} mV · {EffectiveMHz} MHz ({OffsetMHz:+0;-0;0})";
    }

    /// <summary>UI row for one per-game binding: the binding plus the resolved profile name/validity.</summary>
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
            ? "profile deleted — re-bind or remove"
            : ProfileValidated
                ? "auto-switch ready ✓"
                : "not yet validated — apply manually once to enable auto-switch";

        private bool _enabled;

        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }
    }

    /// <summary>One responsive telemetry tile (value + label, rebuilt every tick).</summary>
    public sealed class TelemetryTileRow : ObservableObject
    {
        private string _value = "—";

        public TelemetryTileRow(string label, string value)
        {
            Label = label;
            _value = value;
        }

        public string Label { get; }

        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }

    /// <summary>One monitor-history sample (nulls = not reported that tick).</summary>
    public sealed record MonitorSample(
        int? GpuTempC,
        int? HotspotTempC,
        int? MemTempC,
        double? CoreClockMHz,
        double? MemClockMHz,
        double? PowerDrawW,
        int? FanPercent);
}
