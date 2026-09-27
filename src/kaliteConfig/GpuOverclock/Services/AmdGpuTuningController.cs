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
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// AMD backend for the overclock module, over ADLX (see
    /// <see cref="AmdAdlxInterop"/> for why this needs no native build).
    ///
    /// Implemented: detection, identity, per-control ranges, and writes for core
    /// clock, VRAM frequency, power limit, static fan speed and Zero RPM - each
    /// one clamped to the range the driver itself reports, never to a number
    /// from a spec sheet.
    ///
    /// Not implemented, and each for a stated reason rather than by omission:
    ///   - V/F curve and voltage: reachable, but through the tuning-state structs
    ///     (ADLX_MANUAL_TUNING_STATE), whose layout is large and version-specific.
    ///     Voltage control on a card that cannot be validated here is not a
    ///     feature worth shipping.
    ///   - Fan "restore to automatic": ADLX exposes manual fan STATES and no
    ///     documented call to hand the fan back to the driver's curve. Guessing a
    ///     magic value here risks pinning a GPU's fan at 100%, so the button
    ///     declines with a reason instead.
    ///   - Telemetry: a separate interface chain (PerformanceMonitoring).
    ///
    /// One consequence worth stating: the module's safety machine reverts by
    /// reading anchors and writing them back, which now works for AMD's clock,
    /// memory and power. Fan does not - its revert is "hand the fan back to
    /// automatic", and ADLX has no call for that - so a forced AMD fan speed is
    /// not auto-revertible and the revert reports the failure rather than
    /// pretending. Clamping to the driver's own minimum is what keeps that
    /// bounded.
    ///   - Temperature limit: ADLX's temperature range here is the fan-curve
    ///     threshold, a different control from a GPU thermal limit, and mapping
    ///     one onto the other would be a lie.
    /// </summary>
    public sealed class AmdGpuTuningController : IGpuTuningController
    {
        private readonly object _gate = new();
        private List<AmdGpuTuningSession> _sessions = new();
        private AmdGpuTuningSession? _session;
        private AmdGpuTuningSupport? _primary;

        // Revert anchors, captured once when the tuning domains open. Every
        // write is expressed against these, so "back to default" is a number we
        // actually read rather than one we assumed.
        private AdlxIntRange? _coreRange;
        private AdlxIntRange? _vramRange;
        private AdlxIntRange? _powerRange;
        private AdlxIntRange? _fanRange;
        private int? _coreBaseline;   // ADLX units (MHz), as reported
        private int? _vramBaseline;   // ADLX units (MHz), absolute
        private int? _powerBaseline;  // percent
        private int? _fanBaseline;    // percent

        private bool _coreIsOffsetSemantics;
        private bool _fanSupported;
        private bool _zeroRpmSupported;

        public bool IsInitialized { get; private set; }

        public string? DriverVersion { get; private set; }

        /// <summary>
        /// True when this controller is showing a fabricated card
        /// (<see cref="AmdGpuSimulation"/>). The UI says so on the page, and no
        /// write made in this mode reaches a driver.
        /// </summary>
        public bool IsSimulated { get; private set; }

        /// <summary>What a simulated write would have been, for the change log.</summary>
        public string SimulatedWriteLog => AmdGpuSimulation.IsActive ? AmdGpuSimulation.RecentWrites() : "";

        /// <summary>
        /// Fills the same fields <see cref="ReadAnchors"/> fills, from the
        /// simulated card instead of a driver - so every code path downstream
        /// (capabilities, anchors, clamping, the safety machine) is the real one.
        /// </summary>
        private void ReadSimulatedAnchors()
        {
            // The integrated profile answers "no" to all four capability
            // queries, exactly as a real iGPU does - so every domain stays null
            // and the page renders the dead end rather than a fake slider.
            bool any = AmdGpuSimulation.Kind != AmdGpuSimulation.SimKind.Integrated;

            // Mirrors ReadAnchors exactly: an unreadable-as-offset core range is
            // dropped, not exposed, so the control hides itself.
            if (any)
            {
                AdlxIntRange core = AmdGpuSimulation.CoreRange;
                _coreIsOffsetSemantics = AmdTuningMapping.ReportsOffsetSemantics(core);
                if (_coreIsOffsetSemantics) _coreRange = core;
                else CoreClockUnavailableReason = SimulatedAbsoluteCoreReason;
            }
            _coreBaseline = 0;

            _vramRange = any ? AmdGpuSimulation.VramRange : null;
            _vramBaseline = any ? AmdGpuSimulation.VramBaselineMhz : null;

            _powerRange = any ? AmdGpuSimulation.PowerRange : null;
            _powerBaseline = any ? AmdGpuSimulation.PowerBaselinePercent : null;

            _fanSupported = any;
            _fanRange = any ? AmdGpuSimulation.FanRange : null;
            _fanBaseline = any ? AmdGpuSimulation.FanPercent : null;

            _zeroRpmSupported = any;
        }

        private const string SimulatedAbsoluteCoreReason =
            "This card's driver reports core frequency as an absolute value, not an offset, and ADLX " +
            "does not expose the GPU generation needed to tell the two apart. Tuning it blind could " +
            "leave the card at the wrong clock, so the control stays hidden.";

        /// <summary>The card the controls would apply to. Null until Initialize succeeds.</summary>
        public AmdGpuTuningSupport? Primary => _primary;

        /// <summary>Why a control is missing, for the UI to explain rather than guess.</summary>
        public string? CoreClockUnavailableReason { get; private set; }

        public GpuResult Initialize()
        {
            lock (_gate)
            {
                if (IsInitialized) return GpuResult.Ok();

                // Simulated first, and exclusively: when this branch runs no
                // ADLX call is made at all, so a preview cannot reach hardware
                // even if a real card is present.
                if (AmdGpuSimulation.IsActive)
                {
                    IsSimulated = true;
                    DriverVersion = "ADLX 1.4 (simulated)";
                    _primary = AmdGpuSimulation.Support;
                    ReadSimulatedAnchors();
                    IsInitialized = true;
                    return GpuResult.Ok();
                }

                if (!AmdAdlxInterop.IsAvailable)
                {
                    return GpuResult.Fail(
                        OverclockErrorKind.NvApiInitFailed,
                        "The AMD driver did not provide ADLX (amdadlx64.dll). Reinstall the AMD display driver.");
                }

                _sessions = AmdAdlxInterop.EnumerateTuningSessions();
                if (_sessions.Count == 0)
                    return GpuResult.Fail(OverclockErrorKind.GpuNotDetected, "ADLX reported no AMD GPUs.");

                // Prefer a discrete card: an integrated adapter shares system
                // memory and is a poor tuning target, so if a dGPU exists use it.
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

        /// <summary>
        /// Opens the tuning domains and records what the card is set to right
        /// now. A domain the driver refuses leaves its range null, which the UI
        /// reads as "do not render this control" - so an unsupported control
        /// never appears as a dead slider.
        /// </summary>
        private void ReadAnchors()
        {
            if (_session is null || !_session.IsUsable) return;
            AdlxIntRange? core = _session.ReadCoreMaxFrequencyRange();
            if (core is { } c)
            {
                // Offset semantics only when the range looks like one; see
                // AmdTuningMapping for why this is checked rather than assumed.
                _coreIsOffsetSemantics = AmdTuningMapping.ReportsOffsetSemantics(c);
                if (_coreIsOffsetSemantics)
                {
                    _coreRange = c;
                    _coreBaseline = _session.ReadCoreMaxFrequency();
                }
                else
                {
                    CoreClockUnavailableReason = SimulatedAbsoluteCoreReason;
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
            if (fan is not null) _fanBaseline = _session.ReadFanSpeed();

            _zeroRpmSupported = _session.IsZeroRpmSupported() ?? false;
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
                    DriverVersion = DriverVersion ?? "unknown",
                    // ADLX exposes no notebook flag; an integrated adapter is the
                    // closest honest signal we have for a mobile part.
                    IsNotebook = !_primary.IsDiscrete,
                    PciDeviceId = ParsePciDeviceId(_primary.PnpString),
                });
            }
        }

        /// <summary>Reads "VEN_1002&amp;DEV_13C0" out of a PNP device id.</summary>
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

        /// <summary>
        /// Telemetry needs ADLX's PerformanceMonitoring domain, which is a
        /// separate interface chain. Declining it here is honest: the benchmark
        /// sensor and the graphs show no AMD numbers rather than stale ones.
        /// </summary>
        public GpuResult<GpuTelemetrySnapshot> ReadTelemetry()
            => GpuResult<GpuTelemetrySnapshot>.Fail(
                OverclockErrorKind.ControlUnsupported,
                "AMD telemetry is not wired up yet.");

        public GpuResult<GpuCapabilities> ReadCapabilities()
        {
            lock (_gate)
            {
                if (!IsInitialized || _primary is null)
                    return GpuResult<GpuCapabilities>.Fail(OverclockErrorKind.GpuNotDetected);

                return GpuResult<GpuCapabilities>.Ok(new GpuCapabilities
                {
                    GpuName = _primary.Name,
                    DriverVersion = DriverVersion ?? "unknown",
                    IsNotebook = !_primary.IsDiscrete,

                    // A null range is the documented signal for "do not render
                    // this control", which is exactly right for every domain the
                    // driver declined or we cannot interpret safely.
                    CoreOffsetRangeMHz = _coreRange is { } core
                        ? AmdTuningMapping.ToControlRange(core) : null,
                    // VRAM is absolute on the wire, offset in the UI - the range
                    // the user sees has to be shifted too, or the slider and the
                    // setter disagree about what a number means.
                    MemOffsetRangeMHz = _vramRange is { } vram && _vramBaseline is { } vramBase
                        ? AmdTuningMapping.ToOffsetControlRange(vram, vramBase) : null,
                    PowerLimitRangePercent = _powerRange is { } power
                        ? AmdTuningMapping.ToControlRange(power) : null,

                    // ADLX has no GPU thermal limit; the temperature range it
                    // reports is the fan-curve threshold, a different control.
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

        // ---- read-back anchors -------------------------------------------------
        // The safety machine arms a revert from these. If a read declines, no
        // revert is armed for that control - it will not pretend to know a
        // default it did not read.

        public GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets()
        {
            lock (_gate)
            {
                if (!Usable)
                    return GpuResult<(int, int)>.Fail(OverclockErrorKind.GpuNotDetected);

                // Usable already proved there is a session unless we are
                // simulating; the null-conditional keeps the simulated case (and
                // the compiler) honest about which path is live.
                AmdGpuTuningSession? session = _session;
                int? coreNow = IsSimulated
                    ? AmdGpuSimulation.CoreOffsetMhz
                    : _coreIsOffsetSemantics ? session?.ReadCoreMaxFrequency() : null;
                int? vramNow = IsSimulated
                    ? AmdGpuSimulation.VramBaselineMhz + AmdGpuSimulation.MemOffsetMhz
                    : session?.ReadMaxVramFrequency();

                if (coreNow is null && vramNow is null)
                    return GpuResult<(int, int)>.Fail(
                        OverclockErrorKind.ControlUnsupported, "No clock anchor available on this card.");

                // The tuple has no way to say "this half is unknown", so an
                // unreadable core reads as 0. That is safe rather than merely
                // convenient: a revert would then ask for a core offset of 0,
                // and a card with no core range refuses the write outright
                // instead of having 0 MHz written to it.
                return GpuResult<(int, int)>.Ok((
                    // In offset semantics the driver already speaks our units.
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

        // ---- writes ------------------------------------------------------------
        // Every setter re-clamps against the driver's own range. The UI already
        // clamps, but a batch can carry a value that was legal when the profile
        // was saved and is not legal on this card today.

        public GpuResult SetCoreOffsetMhz(int offsetMhz)
        {
            lock (_gate)
            {
                if (!Ready(out GpuResult failure)) return failure;
                if (_coreRange is not { } range)
                    return Unsupported(CoreClockUnavailableReason ?? "Core clock tuning is not available on this card.");
                if (!AmdTuningMapping.WithinRange(range, offsetMhz))
                    return WriteRejected($"Core offset {offsetMhz} MHz is outside the driver's range ({range}).");

                // The driver's range is already in offset units here, so the
                // value goes straight through - snapped to the driver's step.
                int value = AmdTuningMapping.ClampToRange(range, offsetMhz);
                if (IsSimulated) return Simulated($"core clock {value:+#;-#;0} MHz", () => AmdGpuSimulation.CoreOffsetMhz = value);

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

                // ADLX speaks absolute MHz here; the UI speaks offset. The
                // range check is deliberately against the TRANSLATED value -
                // checking the offset against an absolute range would compare
                // two different units and mean nothing.
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
                if (IsSimulated) return Simulated($"fan speed {value}%", () => AmdGpuSimulation.FanPercent = value);

                return _session!.TrySetFanSpeed(value)
                    ? GpuResult.Ok()
                    : WriteRejected("The AMD driver refused the fan speed change.");
            }
        }

        /// <summary>
        /// Declines on purpose. ADLX's fan domain exposes manual states and no
        /// documented "hand the fan back to the driver curve" call; the only
        /// whole-domain reset is ResetToFactory, which would also wipe power and
        /// clock settings. A wrong guess here leaves a GPU's fan pinned.
        /// </summary>
        public GpuResult RestoreFanAuto()
            => Unsupported(
                "ADLX has no documented way to return the fan to automatic. " +
                "Restore it in Radeon Software, or use Reset to factory on the Drivers page.");

        // ---- Zero RPM ------------------------------------------------------------

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

                // Stopping the fan is the one write here whose failure mode is
                // silent - a card that hangs with its fan stopped needs someone
                // to physically visit it. So the state must be readable before we
                // touch it, and readable again after: an unverifiable toggle is
                // worse than no toggle, because it shows "on" for a fan that is
                // still spinning (or the reverse, which is the dangerous one).
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

        // ---- not implemented, with reasons ---------------------------------------

        public GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve()
            => Decline<GpuVoltageFrequencyCurve>(
                "Voltage and V/F curve tuning needs ADLX's tuning-state structs, which are not implemented.");

        public GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz) => Decline();

        public GpuResult<int[]> ReadVfCurveOffsets() => Decline<int[]>();

        public GpuResult<uint> ReadVoltageBoostPercent()
            => Decline<uint>("Voltage boost percentage is an NVIDIA control; ADLX has no equivalent.");

        public GpuResult SetVoltageBoostPercent(uint percent) => Decline();

        // ---- helpers -------------------------------------------------------------

        /// <summary>
        /// Whether a read or write has somewhere to go: a live session, or the
        /// simulation standing in for one.
        /// </summary>
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

        /// <summary>
        /// Records a change that would have been written to a driver, and
        /// applies it to the simulated card. Everything upstream of this - range
        /// clamping, the offset/absolute translation, refusal when unsupported -
        /// has already run for real, which is the point: the preview shows the
        /// same decisions a Radeon would get, minus the silicon.
        /// </summary>
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
