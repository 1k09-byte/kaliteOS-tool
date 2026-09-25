using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.GpuOverclock.Models;
using NvAPIWrapper;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using NvAPIWrapper.GPU;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Real INvidiaGpuController over NvAPIWrapper (LLT.NvAPIWrapper.Net fork).
    /// All NVAPI access is serialized behind a lock (NVAPI is not thread-safe).
    /// Expected failures come back as GpuResult values; only truly exceptional
    /// conditions throw.
    /// </summary>
    public sealed class NvApiGpuController : INvidiaGpuController
    {
        private readonly object _gate = new();
        private bool _initialized;
        private PhysicalGPU? _gpu;

        public bool IsInitialized
        {
            get { lock (_gate) return _initialized; }
        }

        public GpuResult Initialize()
        {
            lock (_gate)
            {
                if (_initialized) return GpuResult.Ok();
                try
                {
                    NVIDIA.Initialize();
                    _initialized = true;
                    return GpuResult.Ok();
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(OverclockErrorKind.NvApiInitFailed, $"NVAPI status: {ex.Status}");
                }
                catch (Exception ex)
                {
                    // Missing/broken nvapi.dll lands here (DllNotFoundException/BadImageFormat).
                    return GpuResult.Fail(OverclockErrorKind.NvApiInitFailed, ex.GetType().Name);
                }
            }
        }

        // ---------- identity ----------

        public GpuResult<GpuIdentity> GetIdentity()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<GpuIdentity>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    var gpu = _gpu!;
                    uint devId = 0;
                    try { devId = gpu.BusInformation?.PCIIdentifiers?.DeviceId ?? 0; } catch { /* bus query optional */ }
                    return GpuResult<GpuIdentity>.Ok(new GpuIdentity
                    {
                        FullName = gpu.FullName ?? "NVIDIA GPU",
                        DriverVersion = FormatDriverVersion(NVIDIA.DriverVersion),
                        IsNotebook = gpu.SystemType == SystemType.Laptop,
                        PciDeviceId = devId & 0xFFFF,
                    });
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<GpuIdentity>.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        internal static string FormatDriverVersion(uint raw)
        {
            // NVAPI packs the version as major*100 + minor (56636 -> "566.36").
            return raw == 0 ? "unknown" : $"{raw / 100}.{raw % 100:00}";
        }

        // ---------- telemetry ----------

        public GpuResult<GpuTelemetrySnapshot> ReadTelemetry()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<GpuTelemetrySnapshot>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    var gpu = _gpu!;

                    double? coreMhz = null, memMhz = null;
                    try
                    {
                        var clocks = gpu.CurrentClockFrequencies;
                        if (clocks.GraphicsClock.IsPresent) coreMhz = clocks.GraphicsClock.Frequency / 1000.0;
                        if (clocks.MemoryClock.IsPresent) memMhz = clocks.MemoryClock.Frequency / 1000.0;
                    }
                    catch (NVIDIAApiException) { /* some domains refuse on some cards */ }

                    int? gpuTemp = null, hotspot = null, memTemp = null;
                    try
                    {
                        var sensors = gpu.ThermalInformation?.ThermalSensors?.ToList();
                        var core = sensors?.FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);
                        if (core != null) gpuTemp = core.CurrentTemperature;
                        // Hotspot: a second GPU-target sensor, when the driver exposes one.
                        var second = sensors?.Where(s => s.Target == ThermalSettingsTarget.GPU).Skip(1).FirstOrDefault();
                        if (second != null) hotspot = second.CurrentTemperature;
                        // VRAM temperature: exposed as a Memory-target sensor on
                        // some boards/drivers; usually absent on consumer cards.
                        var mem = sensors?.FirstOrDefault(s => s.Target == ThermalSettingsTarget.Memory);
                        if (mem != null) memTemp = mem.CurrentTemperature;
                    }
                    catch (NVIDIAApiException) { }

                    double? voltageMv = null;
                    try
                    {
                        voltageMv = GPUApi.GetCurrentVoltage(gpu.Handle).ValueInMicroVolt / 1000.0;
                    }
                    catch (NVIDIAApiException) { } // private API - refused on some driver builds

                    double? powerPct = null;
                    try
                    {
                        var entry = gpu.PowerTopologyInformation?.PowerTopologyEntries?
                            .FirstOrDefault(e => e.Domain == PowerTopologyDomain.GPU);
                        if (entry != null) powerPct = entry.PowerUsageInPercent;
                    }
                    catch (NVIDIAApiException) { }

                    // Absolute watts come from nvml.dll (what nvidia-smi itself links
                    // against). NVAPI alone only exposes % of the current limit.
                    double? powerW = null;
                    if (NvmlBridge.TryReadPowerDrawMw(out var mw))
                    {
                        powerW = mw / 1000.0;
                    }

                    // PCIe throughput: NVML exposes cumulative KB counters;
                    // differenced across ticks here into KB/s (null until the
                    // second tick, on counter reset, or when refused).
                    var (pcieTx, pcieRx) = ReadPcieRates();

                    int? fanRpm = null, fanPct = null;
                    try
                    {
                        // Client fan-coolers status first (modern cards report here).
                        // NOTE: FanCoolersStatusEntry is a STRUCT - FirstOrDefault()
                        // would give Nullable<T> with no members; index instead.
                        var fanEntries = GPUApi.GetClientFanCoolersStatus(gpu.Handle).FanCoolersStatusEntries;
                        if (fanEntries is { Length: > 0 })
                        {
                            var fe = fanEntries[0];
                            fanPct = (int)fe.CurrentLevel;
                            fanRpm = fe.CurrentRPM > 0 ? (int)fe.CurrentRPM : null;
                        }
                    }
                    catch (NVIDIAApiException) { }
                    if (fanPct is null)
                    {
                        try
                        {
                            var coolerInfo = gpu.CoolerInformation;
                            fanRpm = coolerInfo.CurrentFanSpeedInRPM;
                            fanPct = coolerInfo.CurrentFanSpeedLevel;
                        }
                        catch (NVIDIAApiException) { } // AIB cards without NVCPL cooler reporting
                    }
                    if (fanPct is < 0) fanPct = null;
                    if (fanRpm is < 0) fanRpm = null;

                    int? usage = null;
                    try { usage = gpu.UsageInformation?.GPU?.Percentage; } catch (NVIDIAApiException) { }

                    double? vramUsedMb = null;
                    try
                    {
                        var mem = gpu.MemoryInformation;
                        if (mem != null)
                        {
                            var totalKb = (double)mem.DedicatedVideoMemoryInkB;
                            var availKb = (double)mem.CurrentAvailableDedicatedVideoMemoryInkB;
                            vramUsedMb = Math.Max(0.0, totalKb - availKb) / 1024.0;
                        }
                    }
                    catch (NVIDIAApiException) { }

                    int? pcieGen = null, pcieWidth = null;
                    try
                    {
                        var state = gpu.PerformanceStatesInfo?.CurrentPerformanceState;
                        var pcie = state?.PCIeInformation;
                        if (pcie != null) { pcieGen = (int)pcie.Generation; pcieWidth = (int)pcie.Lanes; }
                    }
                    catch (NVIDIAApiException) { }

                    return GpuResult<GpuTelemetrySnapshot>.Ok(new GpuTelemetrySnapshot
                    {
                        Timestamp = DateTime.Now,
                        CoreClockMHz = coreMhz,
                        MemClockMHz = memMhz,
                        GpuTempC = gpuTemp,
                        HotspotTempC = hotspot,
                        MemTempC = memTemp,
                        VoltageMv = voltageMv,
                        PowerDrawW = powerW,
                        PowerDrawPercentOfLimit = powerPct,
                        FanRpm = fanRpm,
                        FanPercent = fanPct,
                        GpuUsagePercent = usage,
                        VramUsageMb = vramUsedMb,
                        PcieGen = pcieGen,
                        PcieWidth = pcieWidth,
                        PcieTxKBs = pcieTx,
                        PcieRxKBs = pcieRx,
                    });
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<GpuTelemetrySnapshot>.Fail(MapStatus(ex), ex.Status.ToString());
                }
                catch (Exception ex)
                {
                    return GpuResult<GpuTelemetrySnapshot>.Fail(OverclockErrorKind.Unknown, ex.GetType().Name);
                }
            }
        }

        // Cached absolute power limit (watts) from NVML so %-of-limit can be
        // cross-checked; refreshed on ReadCapabilities.
        internal double? _powerLimitWCache;

        // Previous NVML PCIe counters for rate computation (always touched
        // under _gate, same as every other controller field).
        private uint? _pciePrevTxKb, _pciePrevRxKb;
        private DateTime _pciePrevAt = DateTime.MinValue;

        /// <summary>
        /// Differences the cumulative NVML PCIe counters across ticks into
        /// KB/s rates. Null until the second successful tick, when a counter
        /// resets, or when NVML refuses the counters.
        /// </summary>
        private (double? TxKBs, double? RxKBs) ReadPcieRates()
        {
            try
            {
                if (!NvmlBridge.TryReadPcieThroughputKb(0, out uint tx)
                    || !NvmlBridge.TryReadPcieThroughputKb(1, out uint rx))
                    return (null, null);
                var now = DateTime.UtcNow;
                double? txRate = null, rxRate = null;
                if (_pciePrevTxKb.HasValue && _pciePrevRxKb.HasValue)
                {
                    double dt = (now - _pciePrevAt).TotalSeconds;
                    if (dt > 0.2 && tx >= _pciePrevTxKb.Value && rx >= _pciePrevRxKb.Value)
                    {
                        txRate = (tx - _pciePrevTxKb.Value) / dt;
                        rxRate = (rx - _pciePrevRxKb.Value) / dt;
                    }
                }
                _pciePrevTxKb = tx;
                _pciePrevRxKb = rx;
                _pciePrevAt = now;
                return (txRate, rxRate);
            }
            catch
            {
                return (null, null);
            }
        }

        // ---------- capabilities ----------

        public GpuResult<GpuCapabilities> ReadCapabilities()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<GpuCapabilities>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    var gpu = _gpu!;

                    OverclockControlRange? coreRange = null, memRange = null;
                    double? powerPctCur = null;
                    OverclockControlRange? powerRange = null;
                    OverclockControlRange? tempRange = null;
                    int? tempCur = null;
                    bool fanSupported = false;

                    // Clock delta ranges live on the P0 state's clock entries.
                    try
                    {
                        var info = GPUApi.GetPerformanceStates20(gpu.Handle);
                        if (info.PerformanceStates != null)
                        {
                            var p0 = info.PerformanceStates.FirstOrDefault(s => s.StateId == PerformanceStateId.P0_3DPerformance);
                            if (p0 != null && info.Clocks.TryGetValue(p0.StateId, out var entries))
                            {
                                foreach (var e in entries)
                                {
                                    if (e.DomainId == PublicClockDomain.Graphics && e.IsEditable)
                                        coreRange = DeltaRangeToMhz(e);
                                    if (e.DomainId == PublicClockDomain.Memory && e.IsEditable)
                                        memRange = DeltaRangeToMhz(e);
                                }
                            }
                        }
                    }
                    catch (NVIDIANotSupportedException) { }
                    catch (NVIDIAApiException ex) when (ex.Status == Status.NotSupported) { }

                    // Power + thermal limits via the performance-control block.
                    try
                    {
                        var pc = gpu.PerformanceControl;
                        var pl = pc.PowerLimitInformation?.FirstOrDefault();
                        if (pl != null && pl.MaximumPowerInPercent > pl.MinimumPowerInPercent)
                            powerRange = new OverclockControlRange(
                                Math.Round(pl.MinimumPowerInPercent, 1),
                                Math.Round(pl.MaximumPowerInPercent, 1),
                                1);
                        powerPctCur = pc.PowerLimitPolicies?.FirstOrDefault()?.PowerTargetInPercent;

                        var tl = pc.ThermalLimitInformation?.FirstOrDefault();
                        if (tl != null && tl.MaximumTemperature > tl.MinimumTemperature)
                            tempRange = new OverclockControlRange(tl.MinimumTemperature, tl.MaximumTemperature, 1);
                        tempCur = pc.ThermalLimitPolicies?.FirstOrDefault()?.TargetTemperature;
                    }
                    catch (NVIDIAApiException) { }

                    try
                    {
                        var coolers = gpu.CoolerInformation?.Coolers?.ToList();
                        fanSupported = coolers?.Any(c => c.CoolerController != CoolerController.None
                                                          && c.ControlMode != CoolerControlMode.None) == true;
                    }
                    catch (NVIDIAApiException) { }

                    // The client fan-coolers API is the authoritative modern
                    // signal: if it answers with entries, the fan is controllable
                    // even when the legacy cooler block claims otherwise.
                    if (!fanSupported)
                    {
                        try
                        {
                            var fs = GPUApi.GetClientFanCoolersStatus(gpu.Handle);
                            fanSupported = fs.FanCoolersStatusEntries is { Length: > 0 };
                        }
                        catch (NVIDIAApiException) { }
                    }

                    // Absolute power limit in watts from NVML (powers the W-column).
                    if (NvmlBridge.TryReadDefaultPowerLimitMw(out var defMw)) _powerLimitWCache = defMw / 1000.0;

                    // ---- v2 probes: V/F curve + voltage boost (support-detected, never assumed) ----
                    bool vfSupported = false;
                    int vfCount = 0;
                    try
                    {
                        var gr = BoostTableRange(PublicClockDomain.Graphics);
                        if (gr is not null && gr.Value.Last > gr.Value.First)
                        {
                            // Any V/F base source answering means the curve UI can render.
                            try
                            {
                                var st = GPUApi.GetClientClkVFPointsStatus(gpu.Handle, 1);
                                if (st.Points is { Length: > 0 }) vfSupported = true;
                            }
                            catch (NVIDIAApiException) { }
                            if (!vfSupported)
                            {
                                try
                                {
                                    var vfp = GPUApi.GetVFPCurve(gpu.Handle);
                                    if (vfp.GPUCurveEntries is { Length: > 0 }) vfSupported = true;
                                }
                                catch (NVIDIAApiException) { }
                            }
                            if (vfSupported) vfCount = gr.Value.Last - gr.Value.First + 1;
                        }
                    }
                    catch (NVIDIAApiException) { }

                    bool vbSupported = false;
                    try
                    {
                        _ = GPUApi.GetCoreVoltageBoostPercent(gpu.Handle);
                        vbSupported = true;
                    }
                    catch (NVIDIAApiException) { }

                    return GpuResult<GpuCapabilities>.Ok(new GpuCapabilities
                    {
                        GpuName = gpu.FullName ?? "NVIDIA GPU",
                        DriverVersion = FormatDriverVersion(NVIDIA.DriverVersion),
                        IsNotebook = gpu.SystemType == SystemType.Laptop,
                        CoreOffsetRangeMHz = coreRange,
                        MemOffsetRangeMHz = memRange,
                        PowerLimitRangePercent = powerRange,
                        TempLimitRangeC = tempRange,
                        CurrentPowerLimitPercent = powerPctCur,
                        CurrentTempLimitC = tempCur,
                        FanControlSupported = fanSupported,
                        VfCurveSupported = vfSupported,
                        VfCurvePointCount = vfCount,
                        VoltageBoostSupported = vbSupported,
                    });
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<GpuCapabilities>.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        private static OverclockControlRange? DeltaRangeToMhz(IPerformanceStates20ClockEntry e)
        {
            // FrequencyDeltaInkHz and its DeltaRange are structs - always present.
            var d = e.FrequencyDeltaInkHz;
            var r = d.DeltaRange;
            if (r.Maximum <= r.Minimum) return null;
            return new OverclockControlRange(r.Minimum / 1000.0, r.Maximum / 1000.0, 1);
        }

        // ---------- current values (revert anchors / readback) ----------

        public GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<(int, int)>.Fail(fail.ErrorKind, fail.Detail);

                // Modern drivers: the applied offset lives in the V/F boost
                // table (all domain points shift together). The legacy PStates20
                // delta read keeps reporting 0 there, so it cannot be the
                // readback source.
                try
                {
                    var coreRange = BoostTableRange(PublicClockDomain.Graphics);
                    var memRange = BoostTableRange(PublicClockDomain.Memory);
                    if (coreRange is not null || memRange is not null)
                    {
                        var table = GPUApi.GetClockBoostTable(_gpu!.Handle, 1);
                        var deltas = table.GPUDeltas;

                        // All points of a domain move together, but a domain's
                        // reported range can include unwritable padding points
                        // that stay 0 (measured: memory [127..131] with only
                        // [130..131] live). The applied uniform delta is the
                        // entry with the largest magnitude in the range.
                        static int DomainDelta(PrivateClockBoostTableV1.GPUDelta[] d, int first, int last)
                        {
                            int best = 0;
                            for (int i = Math.Max(0, first); i < d.Length && i <= last; i++)
                            {
                                int v = d[i].FrequencyDeltaInkHz;
                                if (Math.Abs(v) > Math.Abs(best)) best = v;
                            }
                            return best / 1000;
                        }

                        int core = coreRange is { } cr ? DomainDelta(deltas, cr.First, cr.Last) : 0;
                        int mem = memRange is { } mr ? DomainDelta(deltas, mr.First, mr.Last) : 0;
                        return GpuResult<(int, int)>.Ok((core, mem));
                    }
                }
                catch (NVIDIAApiException) { /* fall through to legacy read */ }

                // Legacy drivers: the PStates20 delta read.
                try
                {
                    var info = GPUApi.GetPerformanceStates20(_gpu!.Handle);
                    int core = 0, mem = 0;
                    var p0 = info.PerformanceStates?.FirstOrDefault(s => s.StateId == PerformanceStateId.P0_3DPerformance);
                    if (p0 != null && info.Clocks.TryGetValue(p0.StateId, out var entries))
                    {
                        foreach (var e in entries)
                        {
                            var d = e.FrequencyDeltaInkHz; // struct copy - no null
                            if (e.DomainId == PublicClockDomain.Graphics) core = d.DeltaValue;
                            if (e.DomainId == PublicClockDomain.Memory) mem = d.DeltaValue;
                        }
                    }
                    return GpuResult<(int, int)>.Ok((core / 1000, mem / 1000));
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<(int, int)>.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<(double, int?)>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    double? power = null;
                    int? temp = null;
                    try { power = _gpu!.PerformanceControl.PowerLimitPolicies?.FirstOrDefault()?.PowerTargetInPercent; }
                    catch (NVIDIAApiException) { }
                    try { temp = _gpu!.PerformanceControl.ThermalLimitPolicies?.FirstOrDefault()?.TargetTemperature; }
                    catch (NVIDIAApiException) { }
                    return GpuResult<(double, int?)>.Ok((power ?? 100, temp));
                }
                catch (Exception ex)
                {
                    return GpuResult<(double, int?)>.Fail(OverclockErrorKind.Unknown, ex.GetType().Name);
                }
            }
        }

        // ---------- writes ----------

        public GpuResult SetCoreOffsetMhz(int offsetMhz)
            => SetClockOffset(PublicClockDomain.Graphics, offsetMhz);

        public GpuResult SetMemoryOffsetMhz(int offsetMhz)
            => SetClockOffset(PublicClockDomain.Memory, offsetMhz);

        /// <summary>
        /// Point ranges of the clock boost table per public clock domain, from
        /// GetClockBoostRanges. On a RTX 4070 SUPER this is Graphics=[0..126]
        /// and Memory=[127..131]; indices move with the GPU so they are always
        /// queried live.
        /// </summary>
        private (int First, int Last)? BoostTableRange(PublicClockDomain domain)
        {
            try
            {
                var ranges = GPUApi.GetClockBoostRanges(_gpu!.Handle);
                foreach (var r in ranges.ClockBoostRanges)
                {
                    if (r.ClockDomain == domain)
                        return ((int)r.FirstPointIndex, (int)r.LastPointIndex);
                }
                return null;
            }
            catch (NVIDIAApiException)
            {
                return null;
            }
        }

        /// <summary>
        /// Editable delta range (MHz) for one public clock domain as reported by
        /// PStates20 - the same source ReadCapabilities uses, so the clamp here
        /// and the UI slider range can never disagree. Null when not queryable.
        /// </summary>
        private (int MinMhz, int MaxMhz)? QueryDeltaRangeMhz(PublicClockDomain domain)
        {
            try
            {
                var info = GPUApi.GetPerformanceStates20(_gpu!.Handle);
                var p0 = info.PerformanceStates?.FirstOrDefault(s => s.StateId == PerformanceStateId.P0_3DPerformance);
                if (p0 != null && info.Clocks.TryGetValue(p0.StateId, out var entries))
                {
                    foreach (var e in entries)
                    {
                        if (e.DomainId != domain || !e.IsEditable) continue;
                        var r = e.FrequencyDeltaInkHz.DeltaRange;
                        if (r.Maximum > r.Minimum)
                            return ((int)Math.Round(r.Minimum / 1000.0), (int)Math.Round(r.Maximum / 1000.0));
                    }
                }
            }
            catch (NVIDIAApiException) { }
            return null;
        }

        /// <summary>
        /// Applies a whole-domain clock offset by shifting the V/F curve: every
        /// boost-table point of the domain moves by the same delta. This is the
        /// write path modern drivers still accept - the PStates20 delta write
        /// (SetPerformanceStates20) returns NotSupported on current drivers
        /// even when its read side reports editable ranges.
        ///
        /// Defense-in-depth (phase-2 finding): the driver ACCEPTS out-of-range
        /// deltas at the API level without error, so every write is clamped to
        /// the driver-queried range first. This also protects profile applies,
        /// where the value comes from a saved file rather than a range-limited
        /// slider.
        /// </summary>
        private GpuResult SetClockOffset(PublicClockDomain domain, int offsetMhz)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                try
                {
                    var allowed = QueryDeltaRangeMhz(domain);
                    if (allowed is { } al)
                        offsetMhz = Math.Clamp(offsetMhz, al.MinMhz, al.MaxMhz);

                    var range = BoostTableRange(domain);
                    if (range is null) return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);

                    // Build the table from the driver's CURRENT state so the
                    // other domain's offsets are preserved untouched.
                    var table = GPUApi.GetClockBoostTable(_gpu.Handle, 1);
                    var src = table.GPUDeltas;
                    var deltas = new PrivateClockBoostTableV1.GPUDelta[src.Length];
                    int deltaKhz = offsetMhz * 1000;
                    for (int i = 0; i < src.Length; i++)
                    {
                        bool inDomain = i >= range.Value.First && i <= range.Value.Last;
                        deltas[i] = new PrivateClockBoostTableV1.GPUDelta(inDomain ? deltaKhz : src[i].FrequencyDeltaInkHz);
                    }

                    GPUApi.SetClockBoostTable(_gpu.Handle, new PrivateClockBoostTableV1(deltas), 1);
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult SetPowerLimitPercent(double percent)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                try
                {
                    // Clamp to the driver-reported power-limit range (same source
                    // as ReadCapabilities) before writing.
                    var pl = _gpu!.PerformanceControl.PowerLimitInformation?.FirstOrDefault();
                    if (pl != null && pl.MaximumPowerInPercent > pl.MinimumPowerInPercent)
                        percent = Math.Clamp(percent, pl.MinimumPowerInPercent, pl.MaximumPowerInPercent);

                    var status = GPUApi.ClientPowerPoliciesGetStatus(_gpu!.Handle);
                    var entries = status.PowerPolicyStatusEntries;
                    if (entries is null || entries.Length == 0)
                        return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);

                    uint pcm = (uint)Math.Round(Math.Clamp(percent, 1, 200) * 1000);
                    var newEntries = entries
                        .Select(e => e.PerformanceStateId == PerformanceStateId.P0_3DPerformance
                            ? new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry(pcm)
                            : e)
                        .ToArray();
                    GPUApi.ClientPowerPoliciesSetStatus(_gpu.Handle, new PrivatePowerPoliciesStatusV1(newEntries));
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult SetTempLimitC(int tempLimitC)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                try
                {
                    // Clamp to the driver-reported thermal-limit range (same
                    // source as ReadCapabilities) before writing.
                    var tl = _gpu!.PerformanceControl.ThermalLimitInformation?.FirstOrDefault();
                    if (tl != null && tl.MaximumTemperature > tl.MinimumTemperature)
                        tempLimitC = (int)Math.Clamp((double)tempLimitC, tl.MinimumTemperature, tl.MaximumTemperature);

                    var status = GPUApi.GetThermalPoliciesStatus(_gpu!.Handle);
                    var entries = status.ThermalPoliciesStatusEntries;
                    if (entries is null || entries.Length == 0)
                        return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);

                    // Replace the first policy's target, keep the rest untouched.
                    var newEntries = entries.Select((e, i) => i == 0
                        ? new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(e.Controller, tempLimitC)
                        : e).ToArray();
                    GPUApi.SetThermalPoliciesStatus(_gpu.Handle, new PrivateThermalPoliciesStatusV2(newEntries));
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult SetFanStaticPercent(int percent)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                percent = Math.Clamp(percent, 0, 100);

                // Modern path first: the client fan-coolers API (what current
                // NVIDIA tooling and the LLT fork's own fan control drive).
                // Legacy SetCoolerLevels is refused by recent cards/drivers -
                // that was the "driver refused the change to fan speed" failure.
                try
                {
                    var status = GPUApi.GetClientFanCoolersStatus(_gpu!.Handle);
                    var entries = status.FanCoolersStatusEntries;
                    if (entries is { Length: > 0 })
                    {
                        var control = entries
                            .Select(e => new PrivateFanCoolersControlV1.FanCoolersControlEntry(
                                e.CoolerId, FanCoolersControlMode.Manual, (uint)percent))
                            .ToArray();
                        GPUApi.SetClientFanCoolersControl(_gpu.Handle, new PrivateFanCoolersControlV1(control, 0));
                        return GpuResult.Ok();
                    }
                }
                catch (NVIDIANotSupportedException) { }
                catch (NVIDIAApiException) { }

                // Legacy fallback: CoolerPolicy.Manual via the old cooler-levels
                // private API (older cards, e.g. pre-Turing boards).
                try
                {
                    var cooler = _gpu!.CoolerInformation.Coolers?.FirstOrDefault();
                    if (cooler is null) return GpuResult.Fail(OverclockErrorKind.FanControlUnsupported);
                    GPUApi.SetCoolerLevels(_gpu.Handle, (uint)cooler.CoolerId,
                        new PrivateCoolerLevelsV1(new[] { new PrivateCoolerLevelsV1.CoolerLevel(CoolerPolicy.Manual, (uint)percent) }),
                        1);
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.FanControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult RestoreFanAuto()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;

                // Modern path: an explicit FanCoolersControlMode.Auto write -
                // a real hand-back, not "stop writing".
                try
                {
                    var status = GPUApi.GetClientFanCoolersStatus(_gpu!.Handle);
                    var entries = status.FanCoolersStatusEntries;
                    if (entries is { Length: > 0 })
                    {
                        var control = entries
                            .Select(e => new PrivateFanCoolersControlV1.FanCoolersControlEntry(
                                e.CoolerId, FanCoolersControlMode.Auto, 0))
                            .ToArray();
                        GPUApi.SetClientFanCoolersControl(_gpu.Handle, new PrivateFanCoolersControlV1(control, 0));
                        return GpuResult.Ok();
                    }
                }
                catch (NVIDIANotSupportedException) { }
                catch (NVIDIAApiException) { }

                // Legacy fallback: restore default cooler settings.
                try
                {
                    _gpu!.CoolerInformation.RestoreCoolerSettingsToDefault();
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.FanControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        // ---------- voltage / V-F curve (v2) ----------

        /// <summary>
        /// Reads the graphics-domain V/F curve. Base voltages/frequencies come
        /// from the driver's V/F point status (preferred) or the static VFP
        /// curve (fallback); current per-point offsets come from the boost
        /// table; per-point editable ranges come from the boost ranges (with
        /// the PStates20 delta range as fallback).
        ///
        /// INDEX-ALIGNMENT NOTE (verify on hardware): when the V/F status
        /// point count equals the graphics boost-table slice length the join
        /// is 1:1 (the expected shape - both describe the same table). When
        /// counts differ, voltage/frequency labels are proportionally
        /// resampled for DISPLAY ONLY; offsets always address exact
        /// boost-table indices, so the write path is unaffected either way.
        ///
        /// BASE-STABILITY NOTE: the queried base is a session-start anchor.
        /// Callers must cache it and re-read offsets only (never re-derive
        /// the base after an apply), so labels can't drift mid-session.
        /// "Reset to stock" is a zero-delta write, which restores driver
        /// stock regardless of how the base labels were derived.
        /// </summary>
        public GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<GpuVoltageFrequencyCurve>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    var range = BoostTableRange(PublicClockDomain.Graphics);
                    if (range is null) return GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);
                    int first = range.Value.First, last = range.Value.Last;
                    int n = last - first + 1;
                    if (n <= 0) return GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);

                    if (!TryReadVfBaseLabels(out var voltsMv, out var baseMhz))
                        return GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);

                    var pointRanges = QueryPointRangesMhz(PublicClockDomain.Graphics, first, last);
                    if (pointRanges is null) return GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);

                    int[] curDeltaMhz = new int[n];
                    try
                    {
                        var table = GPUApi.GetClockBoostTable(_gpu!.Handle, 1);
                        var src = table.GPUDeltas;
                        for (int i = 0; i < n && first + i < src.Length; i++)
                            curDeltaMhz[i] = src[first + i].FrequencyDeltaInkHz / 1000;
                    }
                    catch (NVIDIAApiException) { /* offsets stay 0 - base still valid */ }

                    bool vbSupported = false;
                    uint vbCurrent = 0;
                    try
                    {
                        vbCurrent = GPUApi.GetCoreVoltageBoostPercent(_gpu!.Handle).Percent;
                        vbSupported = true;
                    }
                    catch (NVIDIAApiException) { }

                    var curve = new GpuVoltageFrequencyCurve
                    {
                        VoltageBoostSupported = vbSupported,
                        CurrentVoltageBoostPercent = vbCurrent,
                    };
                    for (int i = 0; i < n; i++)
                    {
                        int srcIdx = voltsMv.Length == n ? i
                            : (int)((long)i * (voltsMv.Length - 1) / Math.Max(1, n - 1));
                        curve.Points.Add(new VfCurvePoint
                        {
                            VoltageMv = voltsMv[srcIdx],
                            BaseFrequencyMHz = baseMhz[srcIdx],
                            OffsetMHz = curDeltaMhz[i],
                            MinOffsetMHz = pointRanges[i].MinMhz,
                            MaxOffsetMHz = pointRanges[i].MaxMhz,
                        });
                    }
                    return GpuResult<GpuVoltageFrequencyCurve>.Ok(curve);
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult<GpuVoltageFrequencyCurve>.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<GpuVoltageFrequencyCurve>.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        /// <summary>
        /// V/F base labels (voltages in mV, stock frequencies in MHz) from the
        /// live point status, falling back to the static VFP curve. Returns
        /// false when neither source answers.
        /// </summary>
        private bool TryReadVfBaseLabels(out int[] voltsMv, out int[] baseMhz)
        {
            voltsMv = Array.Empty<int>();
            baseMhz = Array.Empty<int>();
            try
            {
                var status = GPUApi.GetClientClkVFPointsStatus(_gpu!.Handle, 1);
                var pts = status.Points;
                if (pts is { Length: > 0 })
                {
                    voltsMv = new int[pts.Length];
                    baseMhz = new int[pts.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        voltsMv[i] = pts[i].VoltageInMilliV > 0
                            ? (int)pts[i].VoltageInMilliV
                            : (int)(pts[i].VoltageInMicroV / 1000);
                        baseMhz[i] = (int)Math.Round(pts[i].FrequencyInkHz / 1000.0);
                    }
                    return true;
                }
            }
            catch (NVIDIAApiException) { }
            try
            {
                var vfp = GPUApi.GetVFPCurve(_gpu!.Handle);
                var entries = vfp.GPUCurveEntries;
                if (entries is { Length: > 0 })
                {
                    voltsMv = new int[entries.Length];
                    baseMhz = new int[entries.Length];
                    for (int i = 0; i < entries.Length; i++)
                    {
                        voltsMv[i] = (int)(entries[i].VoltageInMicroV / 1000);
                        baseMhz[i] = (int)Math.Round(entries[i].FrequencyInkHz / 1000.0);
                    }
                    return true;
                }
            }
            catch (NVIDIAApiException) { }
            return false;
        }

        /// <summary>
        /// Per-point editable offset ranges (MHz) for one clock domain's
        /// boost-table slice. Primary source is the boost-range entries
        /// covering each point; entries whose min/max look like ABSOLUTE
        /// frequencies (floor above zero - real delta ranges always extend to
        /// or below zero, since underclocking is allowed) are rejected as
        /// garbage, because clamping an offset to an absolute-frequency range
        /// would catapult the clock. Falls back to the uniform PStates20
        /// delta range; null when neither is queryable.
        /// </summary>
        private List<(int MinMhz, int MaxMhz)>? QueryPointRangesMhz(PublicClockDomain domain, int first, int last)
        {
            int n = last - first + 1;
            if (n <= 0) return null;

            try
            {
                var ranges = GPUApi.GetClockBoostRanges(_gpu!.Handle);
                var list = new List<(int, int)>(n);
                bool allCovered = true;
                for (int i = first; i <= last; i++)
                {
                    bool found = false;
                    foreach (var r in ranges.ClockBoostRanges)
                    {
                        if (r.ClockDomain != domain) continue;
                        if (i < r.FirstPointIndex || i > r.LastPointIndex) continue;
                        // Delta-range discriminator (see doc comment).
                        if (r.MaximumInkHz > r.MinimumInkHz
                            && r.MinimumInkHz <= 0
                            && Math.Abs((long)r.MaximumInkHz) <= 2_000_000
                            && Math.Abs((long)r.MinimumInkHz) <= 2_000_000)
                        {
                            list.Add(((int)Math.Round(r.MinimumInkHz / 1000.0),
                                      (int)Math.Round(r.MaximumInkHz / 1000.0)));
                            found = true;
                            break;
                        }
                    }
                    if (!found) { allCovered = false; break; }
                }
                if (allCovered && list.Count == n) return list;
            }
            catch (NVIDIAApiException) { }

            var uniform = QueryDeltaRangeMhz(domain);
            if (uniform is null) return null;
            var result = new List<(int, int)>(n);
            for (int i = 0; i < n; i++) result.Add((uniform.Value.MinMhz, uniform.Value.MaxMhz));
            return result;
        }

        /// <summary>
        /// Writes per-point graphics-domain offsets (MHz, boost-table point
        /// order). Count must match the live slice; every point is clamped to
        /// its driver-queried range first (the driver accepts out-of-range
        /// deltas without error - same defense-in-depth as SetClockOffset).
        /// Other domains are preserved from current driver state.
        /// Monotonicity is NOT re-checked here - the caller validates
        /// client-side before committing (GpuVoltageFrequencyCurve.ValidateMonotonic).
        /// </summary>
        public GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                try
                {
                    var range = BoostTableRange(PublicClockDomain.Graphics);
                    if (range is null) return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                    int first = range.Value.First, last = range.Value.Last;
                    int n = last - first + 1;
                    if (offsetsMhz.Count != n)
                        return GpuResult.Fail(OverclockErrorKind.WriteRejected,
                            $"V/F curve point count changed (expected {n}, got {offsetsMhz.Count}) - re-read the curve and retry.");

                    var pointRanges = QueryPointRangesMhz(PublicClockDomain.Graphics, first, last);
                    if (pointRanges is null) return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);

                    var table = GPUApi.GetClockBoostTable(_gpu.Handle, 1);
                    var src = table.GPUDeltas;
                    var deltas = new PrivateClockBoostTableV1.GPUDelta[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        if (i >= first && i <= last)
                        {
                            int k = i - first;
                            int clamped = Math.Clamp(offsetsMhz[k], pointRanges[k].MinMhz, pointRanges[k].MaxMhz);
                            deltas[i] = new PrivateClockBoostTableV1.GPUDelta(clamped * 1000);
                        }
                        else
                        {
                            deltas[i] = new PrivateClockBoostTableV1.GPUDelta(src[i].FrequencyDeltaInkHz);
                        }
                    }

                    GPUApi.SetClockBoostTable(_gpu.Handle, new PrivateClockBoostTableV1(deltas), 1);
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        /// <summary>
        /// Reads the CURRENT per-point graphics-domain offsets straight from
        /// the boost table - the revert anchor and resync source for curve
        /// batches. Empty when unsupported.
        /// </summary>
        public GpuResult<int[]> ReadVfCurveOffsets()
        {
            lock (_gate)
            {
                try
                {
                    if (!EnsureGpu(out var fail)) return GpuResult<int[]>.Fail(fail.ErrorKind, fail.Detail);
                    var range = BoostTableRange(PublicClockDomain.Graphics);
                    if (range is null) return GpuResult<int[]>.Ok(Array.Empty<int>());
                    var table = GPUApi.GetClockBoostTable(_gpu!.Handle, 1);
                    var src = table.GPUDeltas;
                    // Insane range from the driver (negative first index past
                    // the end, empty slice): refuse cleanly instead of
                    // indexing out of range (a thrown exception here would
                    // escape the controller's never-throw contract).
                    int count = range.Value.Last - range.Value.First + 1;
                    if (range.Value.First < 0 || range.Value.Last >= src.Length || count <= 0)
                        return GpuResult<int[]>.Fail(OverclockErrorKind.ControlUnsupported);
                    var anchor = new int[count];
                    for (int i = 0; i < anchor.Length && range.Value.First + i < src.Length; i++)
                        anchor[i] = src[range.Value.First + i].FrequencyDeltaInkHz / 1000;
                    return GpuResult<int[]>.Ok(anchor);
                }
                catch (NVIDIAApiException ex) { return GpuResult<int[]>.Fail(MapStatus(ex), ex.Status.ToString()); }
            }
        }

        public GpuResult<uint> ReadVoltageBoostPercent()
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return GpuResult<uint>.Fail(fail.ErrorKind, fail.Detail);
                try
                {
                    return GpuResult<uint>.Ok(GPUApi.GetCoreVoltageBoostPercent(_gpu!.Handle).Percent);
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult<uint>.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult<uint>.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        public GpuResult SetVoltageBoostPercent(uint percent)
        {
            lock (_gate)
            {
                if (!EnsureGpu(out var fail)) return fail;
                try
                {
                    percent = Math.Min(percent, 100);
                    GPUApi.SetCoreVoltageBoostPercent(_gpu!.Handle, new PrivateVoltageBoostPercentV1(percent));
                    return GpuResult.Ok();
                }
                catch (NVIDIANotSupportedException)
                {
                    return GpuResult.Fail(OverclockErrorKind.ControlUnsupported);
                }
                catch (NVIDIAApiException ex)
                {
                    return GpuResult.Fail(MapStatus(ex), ex.Status.ToString());
                }
            }
        }

        // ---------- helpers ----------

        private bool EnsureGpu(out GpuResult fail)
        {
            fail = GpuResult.Ok();
            if (!_initialized)
            {
                var init = InitializeNoLock();
                if (!init.IsSuccess) { fail = init; return false; }
            }

            if (_gpu != null) return true;

            PhysicalGPU[] gpus;
            try { gpus = PhysicalGPU.GetPhysicalGPUs(); }
            catch (NVIDIAApiException ex)
            {
                fail = GpuResult.Fail(OverclockErrorKind.GpuNotDetected, ex.Status.ToString());
                return false;
            }

            if (gpus.Length == 0)
            {
                fail = GpuResult.Fail(OverclockErrorKind.GpuNotDetected);
                return false;
            }

            _gpu = gpus.FirstOrDefault(g => g.GPUType == GPUType.Discrete) ?? gpus[0];
            return true;
        }

        /// <summary>Called when the GPU may have changed (re-detect) - forgets the cached handle.</summary>
        public void InvalidateGpu()
        {
            lock (_gate) _gpu = null;
        }

        private GpuResult InitializeNoLock()
        {
            if (_initialized) return GpuResult.Ok();
            try
            {
                NVIDIA.Initialize();
                _initialized = true;
                return GpuResult.Ok();
            }
            catch (NVIDIAApiException ex)
            {
                return GpuResult.Fail(OverclockErrorKind.NvApiInitFailed, $"NVAPI status: {ex.Status}");
            }
            catch (Exception ex)
            {
                return GpuResult.Fail(OverclockErrorKind.NvApiInitFailed, ex.GetType().Name);
            }
        }

        private static OverclockErrorKind MapStatus(NVIDIAApiException ex) => ex.Status switch
        {
            Status.NotSupported => OverclockErrorKind.ControlUnsupported,
            _ => OverclockErrorKind.WriteRejected,
        };
    }
}
