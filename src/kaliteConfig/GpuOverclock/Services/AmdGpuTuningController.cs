using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class AmdGpuTuningController : IGpuTuningController
    {
        private const int MaxCoreOffsetMHz = 500;
        private readonly object _gate = new();
        private List<AmdGpuTuningSession> _sessions = new();
        private AmdGpuTuningSession? _session;
        private AmdGpuTuningSupport? _primary;

        private AdlxIntRange? _coreRange;
        private AdlxIntRange? _vramRange;
        private AdlxIntRange? _powerRange;
        private AdlxIntRange? _fanRange;
        private int? _coreBaseline;
        private int? _vramBaseline;
        private int? _powerBaseline;
        private int? _fanBaseline;
        private (int TempC, int SpeedPercent)[]? _fanDefaultCurve;
        private bool? _zeroRpmDefault;

        private bool _coreIsOffsetSemantics;
        private bool _fanSupported;
        private bool _zeroRpmSupported;

        public bool IsInitialized { get; private set; }

        public string? DriverVersion { get; private set; }

                private string DisplayDriverVersionText
        {
            get
            {
                if (IsSimulated) return DriverVersion ?? "unknown";
                return DisplayDriverVersion.AmdDisplayDriver ?? DriverVersion ?? "unknown";
            }
        }

        public bool IsSimulated { get; private set; }

        public string SimulatedWriteLog => AmdGpuSimulation.IsActive ? AmdGpuSimulation.RecentWrites() : "";

        private void ReadSimulatedAnchors()
        {
            bool any = AmdGpuSimulation.Kind != AmdGpuSimulation.SimKind.Integrated;

            if (any)
            {
                AdlxIntRange core = AmdGpuSimulation.CoreRange;
                _coreIsOffsetSemantics = AmdTuningMapping.ReportsOffsetSemantics(core);
                if (_coreIsOffsetSemantics)
                {
                    _coreRange = core;
                    _coreBaseline = 0;
                }
                else
                {
                    _coreBaseline = AmdGpuSimulation.CoreBaselineMhz;
                    _coreRange = AmdTuningMapping.ClampOffsetRange(
                        AmdTuningMapping.ToOffsetRange(core, _coreBaseline.Value), MaxCoreOffsetMHz);
                }
            }
            else
            {
                _coreBaseline = 0;
            }

            _vramRange = any ? AmdGpuSimulation.VramRange : null;
            _vramBaseline = any ? AmdGpuSimulation.VramBaselineMhz : null;

            _powerRange = any ? AmdGpuSimulation.PowerRange : null;
            _powerBaseline = any ? AmdGpuSimulation.PowerBaselinePercent : null;

            _fanSupported = any;
            _fanRange = any ? AmdGpuSimulation.FanRange : null;
            _fanBaseline = any ? AmdGpuSimulation.FanPercent : null;
            _fanDefaultCurve = null;

            _zeroRpmSupported = any;
            _zeroRpmDefault = AmdGpuSimulation.ZeroRpm;
        }

        public AmdGpuTuningSupport? Primary => _primary;

        public string? CoreClockUnavailableReason { get; private set; }

        public GpuResult Initialize()
        {
            lock (_gate)
            {
                if (IsInitialized) return GpuResult.Ok();

                if (AmdGpuSimulation.IsActive)
                {
                    IsSimulated = true;
                    DriverVersion = "ADLX 2.0 (simulated)";
                    _primary = AmdGpuSimulation.Support;
                    ReadSimulatedAnchors();
                    IsInitialized = true;
                    return GpuResult.Ok();
                }

                if (!AmdAdlxInterop.IsAvailable)
                {
                    return GpuResult.Fail(
                        OverclockErrorKind.NvApiInitFailed,
                        AmdAdlxInterop.LastInitializationError ??
                        "The AMD driver did not provide ADLX (amdadlx64.dll). Reinstall the AMD display driver.");
                }

                _sessions = AmdAdlxInterop.EnumerateTuningSessions();
                if (_sessions.Count == 0)
                    return GpuResult.Fail(OverclockErrorKind.GpuNotDetected, "ADLX reported no AMD GPUs.");

                _session = _sessions.FirstOrDefault(s => s.Support.IsDiscrete && s.Support.AnyControlSupported)
                        ?? _sessions.FirstOrDefault(s => s.Support.AnyControlSupported)
                        ?? _sessions[0];
                _primary = _session.Support;

                DriverVersion = AmdAdlxInterop.DriverVersion;
                ReadAnchors();
                IsInitialized = true;
                return GpuResult.Ok();
            }
        }

        private void ReadAnchors()
        {
            if (_session is null || !_session.IsUsable) return;
            AdlxIntRange? core = _session.ReadCoreMaxFrequencyRange();
            if (core is { } c)
            {
                _coreIsOffsetSemantics = AmdTuningMapping.ReportsOffsetSemantics(c);
                if (_coreIsOffsetSemantics)
                {
                    _coreRange = AmdTuningMapping.ClampOffsetRange(c, MaxCoreOffsetMHz);
                    _coreBaseline = 0;
                }
                else if (_session.ReadCoreMaxFrequency() is { } baseline && baseline > 0)
                {
                    _coreBaseline = baseline;
                    _coreRange = AmdTuningMapping.ClampOffsetRange(
                        AmdTuningMapping.ToOffsetRange(c, baseline), MaxCoreOffsetMHz);
                }
                else
                {
                    CoreClockUnavailableReason =
                        "The driver reported a maximum-clock range but not the current maximum clock, " +
                        "so a safe offset cannot be calculated.";
                }
            }

            AdlxIntRange? vram = _session.ReadMaxVramFrequencyRange();
            if (vram is { } v)
            {
                _vramRange = v;
                _vramBaseline = _session.ReadMaxVramFrequency();
            }

            AdlxIntRange? power = _session.ReadPowerLimitRange();
            if (power is { } p)
            {
                _powerRange = p;
                _powerBaseline = _session.ReadPowerLimit();
            }

            AdlxIntRange? fan = _session.ReadFanSpeedRange();
            _fanRange = fan;
            _fanSupported = fan is not null;
            if (fan is not null)
            {
                _fanBaseline = _session.ReadFanSpeed();
                _fanDefaultCurve = _session.ReadFanCurve();
            }

            _zeroRpmSupported = _session.IsZeroRpmSupported() ?? false;
            _zeroRpmDefault = _zeroRpmSupported ? _session.ReadZeroRpm() : null;
        }

        public void InvalidateGpu()
        {
            lock (_gate)
            {
                foreach (AmdGpuTuningSession session in _sessions) session.Dispose();
                _sessions = new List<AmdGpuTuningSession>();
                _session = null;
                _primary = null;
                _coreRange = null;
                _vramRange = null;
                _powerRange = null;
                _fanRange = null;
                _coreBaseline = null;
                _vramBaseline = null;
                _powerBaseline = null;
                _fanBaseline = null;
                _fanDefaultCurve = null;
                _zeroRpmDefault = null;
                _coreIsOffsetSemantics = false;
                _fanSupported = false;
                _zeroRpmSupported = false;
                IsInitialized = false;
                IsSimulated = false;
            }
        }

        public GpuResult<GpuIdentity> GetIdentity()
        {
            lock (_gate)
            {
                if (!IsInitialized || _primary is null)
                    return GpuResult<GpuIdentity>.Fail(OverclockErrorKind.GpuNotDetected);

                return GpuResult<GpuIdentity>.Ok(new GpuIdentity
                {
                    FullName = _primary.Name,
                    DriverVersion = DisplayDriverVersionText,
                    IsNotebook = !_primary.IsDiscrete,
                    PciDeviceId = ParsePciDeviceId(_primary.PnpString),
                });
            }
        }

        internal static uint ParsePciDeviceId(string? pnpString)
        {
            if (string.IsNullOrEmpty(pnpString)) return 0;
            foreach (string part in pnpString.Split('&'))
            {
                if (part.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(part.AsSpan(4), System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture, out uint id))
                    return id;
            }
            return 0;
        }

        public GpuResult<GpuTelemetrySnapshot> ReadTelemetry()
        {
            lock (_gate)
            {
                if (!Usable)
                    return GpuResult<GpuTelemetrySnapshot>.Fail(OverclockErrorKind.GpuNotDetected);

                if (IsSimulated)
                    return GpuResult<GpuTelemetrySnapshot>.Ok(BuildSimulatedTelemetry());

                if (!AmdAdlxInterop.TryReadGpuMetrics(_session!.GpuHandle, out AmdGpuMetrics m))
                {
                    return GpuResult<GpuTelemetrySnapshot>.Fail(
                        OverclockErrorKind.ControlUnsupported,
                        "The AMD driver did not return performance metrics for this adapter.");
                }

                double? boardPower = m.BoardPowerW is > 0 ? m.BoardPowerW : m.PowerW;
                return GpuResult<GpuTelemetrySnapshot>.Ok(new GpuTelemetrySnapshot
                {
                    Timestamp = DateTime.Now,
                    CoreClockMHz = m.CoreClockMHz,
                    MemClockMHz = m.MemClockMHz,
                    GpuTempC = m.GpuTempC,
                    HotspotTempC = m.HotspotTempC,
                    VoltageMv = m.VoltageMv,
                    PowerDrawW = boardPower,
                    FanRpm = m.FanRpm,
                    FanPercent = m.FanPercent,
                    GpuUsagePercent = m.GpuUsagePercent,
                    VramUsageMb = m.VramUsedMb,
                });
            }
        }

        private static GpuTelemetrySnapshot BuildSimulatedTelemetry()
        {
            int core = AmdGpuSimulation.CoreBaselineMhz + AmdGpuSimulation.CoreOffsetMhz;
            int mem = AmdGpuSimulation.VramBaselineMhz + AmdGpuSimulation.MemOffsetMhz;
            int temp = 58 + Math.Clamp(AmdGpuSimulation.CoreOffsetMhz / 40, 0, 18);
            double power = 300.0 * AmdGpuSimulation.PowerLimitPercent / 100.0;

            return new GpuTelemetrySnapshot
            {
                Timestamp = DateTime.Now,
                CoreClockMHz = core,
                MemClockMHz = mem,
                GpuTempC = temp,
                HotspotTempC = temp + 12,
                MemTempC = 54,
                VoltageMv = 1050,
                PowerDrawW = power,
                FanPercent = AmdGpuSimulation.FanPercent ?? 33,
                GpuUsagePercent = 42,
                VramUsageMb = 8192,
                PcieGen = 4,
                PcieWidth = 16,
            };
        }

        public GpuResult<GpuCapabilities> ReadCapabilities()
        {
            lock (_gate)
            {
                if (!IsInitialized || _primary is null)
                    return GpuResult<GpuCapabilities>.Fail(OverclockErrorKind.GpuNotDetected);

                return GpuResult<GpuCapabilities>.Ok(new GpuCapabilities
                {
                    GpuName = _primary.Name,
                    DriverVersion = DisplayDriverVersionText,
                    IsNotebook = !_primary.IsDiscrete,
                    IsIntegrated = !_primary.IsDiscrete,

                    CoreOffsetRangeMHz = _coreRange is { } core
                        ? AmdTuningMapping.ToControlRange(core) : null,
                    MemOffsetRangeMHz = _vramRange is { } vram && _vramBaseline is { } vramBase
                        ? AmdTuningMapping.ToOffsetControlRange(vram, vramBase) : null,
                    PowerLimitRangePercent = _powerRange is { } power
                        ? AmdTuningMapping.ToControlRange(power) : null,

                    TempLimitRangeC = null,
                    CurrentPowerLimitPercent = _powerBaseline,
                    CurrentTempLimitC = null,
                    FanControlSupported = _fanSupported,
                    VfCurveSupported = false,
                    VfCurvePointCount = 0,
                    VoltageBoostSupported = false,
                    ZeroRpmSupported = _zeroRpmSupported,
                });
            }
        }

        public GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets()
        {
            lock (_gate)
            {
                if (!Usable)
                    return GpuResult<(int, int)>.Fail(OverclockErrorKind.GpuNotDetected);

                AmdGpuTuningSession? session = _session;
                int? coreRaw = IsSimulated ? null : session?.ReadCoreMaxFrequency();
                int? coreNow = IsSimulated
                    ? AmdGpuSimulation.CoreOffsetMhz
                    : coreRaw is null || _coreRange is null
                        ? null
                        : _coreIsOffsetSemantics
                            ? coreRaw
                            : coreRaw - (_coreBaseline ?? coreRaw.Value);
                int? vramNow = IsSimulated
                    ? AmdGpuSimulation.VramBaselineMhz + AmdGpuSimulation.MemOffsetMhz
                    : session?.ReadMaxVramFrequency();

                if (coreNow is null && vramNow is null)
                    return GpuResult<(int, int)>.Fail(
                        OverclockErrorKind.ControlUnsupported, "No clock anchor available on this card.");

                return GpuResult<(int, int)>.Ok((
                    coreNow ?? 0,
                    vramNow is null || _vramBaseline is null
                        ? 0
                        : AmdTuningMapping.ToOffset(_vramBaseline.Value, vramNow.Value)));
            }
        }

        public GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits()
        {
            lock (_gate)
            {
                if (!Usable)
                    return GpuResult<(double, int?)>.Fail(OverclockErrorKind.GpuNotDetected);

                int? power = IsSimulated ? (int)AmdGpuSimulation.PowerLimitPercent : _session?.ReadPowerLimit();
                if (power is null)
                    return GpuResult<(double, int?)>.Fail(
                        OverclockErrorKind.ControlUnsupported, "No power anchor available on this card.");

                return GpuResult<(double, int?)>.Ok((power.Value, null));
            }
        }

        public GpuResult SetCoreOffsetMhz(int offsetMhz)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (_coreRange is not { } range)
                    return Unsupported(CoreClockUnavailableReason ?? "Core clock tuning is not available on this card.");
                if (!AmdTuningMapping.WithinRange(range, offsetMhz))
                    return WriteRejected($"Core offset {offsetMhz} MHz is outside the driver's range ({range}).");

                int offset = AmdTuningMapping.ClampToRange(range, offsetMhz);
                int value = AmdTuningMapping.ToAbsolute(_coreBaseline ?? 0, offset);
                if (IsSimulated) return Simulated($"core clock {value} MHz ({offset:+#;-#;0} MHz)",
                    () => AmdGpuSimulation.CoreOffsetMhz = offset);

                return _session!.TrySetCoreMaxFrequency(value)
                    ? GpuResult.Ok()
                    : WriteRejected("The AMD driver refused the core clock change.");
            }
        }

        public GpuResult SetMemoryOffsetMhz(int offsetMhz)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (_vramRange is not { } range || _vramBaseline is null)
                    return Unsupported("Memory tuning is not available on this card.");

                int absolute = AmdTuningMapping.ToAbsolute(_vramBaseline.Value, offsetMhz);
                if (!AmdTuningMapping.WithinRange(range, absolute))
                    return WriteRejected(
                        $"A {offsetMhz:+#;-#;0} MHz memory offset would mean {absolute} MHz, " +
                        $"outside the card's {range}.");

                int value = AmdTuningMapping.ClampToRange(range, absolute);
                if (IsSimulated) return Simulated($"memory clock {value} MHz absolute ({offsetMhz:+#;-#;0} MHz offset)",
                    () => AmdGpuSimulation.MemOffsetMhz = offsetMhz);

                return _session!.TrySetMaxVramFrequency(value)
                    ? GpuResult.Ok()
                    : WriteRejected("The AMD driver refused the memory clock change.");
            }
        }

        public GpuResult SetPowerLimitPercent(double percent)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (_powerRange is not { } range)
                    return Unsupported("Power limit tuning is not available on this card.");

                int requested = (int)Math.Round(percent);
                if (!AmdTuningMapping.WithinRange(range, requested))
                    return WriteRejected($"Power limit {requested}% is outside the driver's range ({range}).");

                int value = AmdTuningMapping.ClampToRange(range, requested);
                if (IsSimulated) return Simulated($"power limit {value}%",
                    () => AmdGpuSimulation.PowerLimitPercent = value);

                return _session!.TrySetPowerLimit(value)
                    ? GpuResult.Ok()
                    : WriteRejected("The AMD driver refused the power limit change.");
            }
        }

        public GpuResult SetTempLimitC(int tempLimitC)
            => Unsupported("ADLX does not expose a GPU thermal limit on this card.");

        public GpuResult SetFanStaticPercent(int percent)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (!_fanSupported || _fanRange is not { } range)
                    return Unsupported("This card has no software-controllable fan.");

                if (!AmdTuningMapping.WithinRange(range, percent))
                    return WriteRejected($"Fan speed {percent}% is outside the driver's range ({range}).");

                int value = AmdTuningMapping.ClampToRange(range, percent);
                if (IsSimulated)
                {
                    if (value > 0) AmdGpuSimulation.ZeroRpm = false;
                    return Simulated($"fan speed {value}%", () => AmdGpuSimulation.FanPercent = value);
                }

                if (value > 0 && _zeroRpmSupported && _session!.ReadZeroRpm() == true)
                    _session.TrySetZeroRpm(false);

                return _session!.TrySetFanSpeed(value)
                    ? GpuResult.Ok()
                    : WriteRejected("The AMD driver refused the fan speed change.");
            }
        }

        public GpuResult RestoreFanAuto()
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;

                if (IsSimulated)
                {
                    AmdGpuSimulation.FanPercent = null;
                    AmdGpuSimulation.ZeroRpm = _zeroRpmDefault ?? false;
                    AmdGpuSimulation.Record("fan restored to the driver curve");
                    return GpuResult.Ok();
                }

                var curve = _session!.ReadDefaultFanCurve() ?? _fanDefaultCurve;
                if (curve is null || curve.Length == 0)
                {
                    return Unsupported(
                        "The driver did not expose a default fan curve, so the original curve cannot " +
                        "be restored. Restore the fan in Radeon Software.");
                }

                if (!_session.TrySetFanCurve(curve))
                    return WriteRejected("The AMD driver refused the restore of the fan curve.");

                bool? zeroRpm = _session.ReadDefaultZeroRpm() ?? _zeroRpmDefault;
                if (_zeroRpmSupported && zeroRpm.HasValue)
                    _session.TrySetZeroRpm(zeroRpm.Value);

                return GpuResult.Ok();
            }
        }

        public GpuResult<bool> IsZeroRpmEnabled()
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return GpuResult<bool>.Fail(failure.ErrorKind, failure.Detail);
                if (!_zeroRpmSupported) return GpuResult<bool>.Fail(OverclockErrorKind.ControlUnsupported);
                bool? on = IsSimulated ? AmdGpuSimulation.ZeroRpm : _session!.ReadZeroRpm();
                return on is null
                    ? GpuResult<bool>.Fail(OverclockErrorKind.ControlUnsupported)
                    : GpuResult<bool>.Ok(on.Value);
            }
        }

        public GpuResult SetZeroRpmEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (!_zeroRpmSupported)
                    return Unsupported("This card does not support Zero RPM.");

                bool? before = IsSimulated ? AmdGpuSimulation.ZeroRpm : _session!.ReadZeroRpm();
                if (before is null)
                    return WriteRejected("The AMD driver would not report the fan state, so Zero RPM was left alone.");

                if (IsSimulated)
                {
                    AmdGpuSimulation.ZeroRpm = enabled;
                    AmdGpuSimulation.Record($"Zero RPM {(enabled ? "on" : "off")}");
                    return GpuResult.Ok();
                }

                if (!_session!.TrySetZeroRpm(enabled))
                    return WriteRejected("The AMD driver refused the Zero RPM change.");

                bool? after = _session.ReadZeroRpm();
                if (after != enabled)
                    return GpuResult.Fail(
                        OverclockErrorKind.ReadbackMismatch,
                        "The AMD driver accepted the Zero RPM change but does not report it back - " +
                        "leaving it alone rather than showing a state we cannot confirm.");

                return GpuResult.Ok();
            }
        }

        public GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve()
            => Decline<GpuVoltageFrequencyCurve>(
                "Voltage and V/F curve tuning needs ADLX's tuning-state structs, which are not implemented.");

        public GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz) => Decline();

        public GpuResult<int[]> ReadVfCurveOffsets() => Decline<int[]>();

        public GpuResult<uint> ReadVoltageBoostPercent()
            => Decline<uint>("Voltage boost percentage is an NVIDIA control; ADLX has no equivalent.");

        public GpuResult SetVoltageBoostPercent(uint percent) => Decline();

        private bool Usable => IsInitialized && (IsSimulated || _session is { IsUsable: true });

        private bool Ready(out GpuResult failure)
        {
            if (Usable)
            {
                failure = GpuResult.Ok();
                return true;
            }
            failure = GpuResult.Fail(OverclockErrorKind.GpuNotDetected,
                "The AMD GPU is no longer available - re-detect and try again.");
            return false;
        }

        private static GpuResult Simulated(string description, Action apply)
        {
            apply();
            AmdGpuSimulation.Record(description);
            return GpuResult.Ok();
        }

        private static GpuResult Unsupported(string reason) =>
            GpuResult.Fail(OverclockErrorKind.ControlUnsupported, reason);

        private static GpuResult WriteRejected(string reason) =>
            GpuResult.Fail(OverclockErrorKind.WriteRejected, reason);

        private GpuResult Decline(string? reason = null) => Unsupported(
            reason ?? "This control is not implemented for the AMD backend.");

        private GpuResult<T> Decline<T>(string? reason = null) => GpuResult<T>.Fail(
            OverclockErrorKind.ControlUnsupported,
            reason ?? "This control is not implemented for the AMD backend.");
    }
}
