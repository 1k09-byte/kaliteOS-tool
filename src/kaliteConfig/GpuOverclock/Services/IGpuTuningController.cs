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
using System.Collections.Generic;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>Static identity of the detected NVIDIA GPU.</summary>
    public sealed record GpuIdentity
    {
        public required string FullName { get; init; }
        public required string DriverVersion { get; init; }
        public required bool IsNotebook { get; init; }
        public required uint PciDeviceId { get; init; }
    }

    /// <summary>
    /// The module's only door to a GPU vendor's tuning API. ViewModels and
    /// services never touch NVAPIWrapper types directly - this isolates wrapper
    /// breaking-changes and makes the write path unit-testable via a fake
    /// implementation.
    ///
    /// Vendor-neutral by contract, not by wishful naming: the members are the
    /// knobs the UI can present, and a vendor implementation that cannot reach one
    /// returns <see cref="OverclockErrorKind.ControlUnsupported"/> rather than
    /// throwing or faking a value. The UI already renders that as "This control is
    /// not supported by your GPU or driver", so an unimplemented control needs no
    /// new UI code - it just declines.
    ///
    /// A second implementation is anticipated. AMD exposes its equivalent through
    /// the ADLX SDK (https://github.com/GPUOpen-LibrariesAndSDKs/ADLX), whose
    /// GPUTuning and PowerTuning domains cover core-clock offset, min frequency,
    /// voltage offset, VRAM frequency, power limit and Zero-RPM, plus a
    /// PerformanceMonitoring domain for telemetry. Two mapping notes for whoever
    /// writes it:
    ///   - VRAM frequency is an ABSOLUTE value on ADLX but an OFFSET in MHz here,
    ///     so an AMD implementation has to subtract the current absolute value to
    ///     feed SetMemoryOffsetMhz.
    ///   - Core frequency is an OFFSET from Navi4+ and an ABSOLUTE frequency
    ///     before that, and ADLX exposes no ASIC generation to tell them apart.
    ///     An implementation must detect that from the reported range rather
    ///     than assume; see AmdTuningMapping.
    ///   - An integrated GPU (a Vega/RDNA iGPU) shares system memory and has no
    ///     fan, so most of these members will legitimately decline. That is a
    ///     correct outcome, not a failure.
    ///
    /// Threading model: all methods are synchronous and serialized internally
    /// (the vendor APIs are not thread-safe); callers that must not block the UI
    /// thread (polling loop, apply paths) invoke them via Task.Run.
    /// Expected failures are returned as GpuResult values, never thrown.
    /// </summary>
    public interface IGpuTuningController
    {
        bool IsInitialized { get; }

        /// <summary>Initializes the vendor API exactly once per process. Idempotent.</summary>
        GpuResult Initialize();

        /// <summary>Picks the primary (first discrete) tunable GPU and reads its identity.</summary>
        GpuResult<GpuIdentity> GetIdentity();

        /// <summary>
        /// Drops the cached handle so the next call re-detects. Called when the
        /// display topology changes (a monitor added or removed can change which
        /// adapter is primary) and when the user re-scans.
        /// </summary>
        void InvalidateGpu();

        /// <summary>One telemetry snapshot; null fields mean "not reported by this GPU".</summary>
        GpuResult<GpuTelemetrySnapshot> ReadTelemetry();

        /// <summary>
        /// Per-control ranges from the driver at detection time. Re-query on
        /// GPU re-detect; ranges are not guaranteed stable across driver reloads.
        /// </summary>
        GpuResult<GpuCapabilities> ReadCapabilities();

        /// <summary>Current applied clock deltas in MHz - the revert anchor and readback source.</summary>
        GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets();

        /// <summary>Current power limit (%) and temp limit (°C) - revert anchors.</summary>
        GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits();

        GpuResult SetCoreOffsetMhz(int offsetMhz);
        GpuResult SetMemoryOffsetMhz(int offsetMhz);
        GpuResult SetPowerLimitPercent(double percent);
        GpuResult SetTempLimitC(int tempLimitC);

        /// <summary>Forces a fixed fan percent (writes CoolerPolicy.Manual).</summary>
        GpuResult SetFanStaticPercent(int percent);

        /// <summary>
        /// Explicitly hands fan control back to the driver default - a real
        /// restore call, so a stale forced speed can never persist silently.
        /// </summary>
        GpuResult RestoreFanAuto();

        // ---------- Zero RPM ----------
        //
        // Stopping the fan at idle is one of the most requested tuning controls
        // and the one most cards answer differently, so it gets its own pair
        // rather than being folded into the fan percent. These are DEFAULT
        // interface methods that decline: a vendor that has not wired the knob
        // up inherits an honest "not supported by your GPU or driver", which is
        // what the UI already renders. No implementer is forced to add members
        // it cannot serve.

        /// <summary>Whether the card can stop its fan at idle, and whether it is stopped now.</summary>
        GpuResult<bool> IsZeroRpmEnabled()
            => GpuResult<bool>.Fail(
                OverclockErrorKind.ControlUnsupported,
                "Zero RPM is not available on this GPU or driver.");

        /// <summary>Turns Zero RPM on or off.</summary>
        GpuResult SetZeroRpmEnabled(bool enabled)
            => GpuResult.Fail(
                OverclockErrorKind.ControlUnsupported,
                "Zero RPM is not available on this GPU or driver.");

        // ---------- voltage / V-F curve (v2) ----------
        //
        // NOTE on the v2 brief's async signatures: this interface's contract
        // is synchronous methods serialized behind an internal lock (NVAPI is
        // not thread-safe); callers that must not block the UI thread wrap
        // calls in Task.Run. The V/F members follow that same convention
        // rather than introducing a second async convention + a new
        // WriteResult type - GpuResult is the module's established shape.

        /// <summary>
        /// Reads the graphics-domain V/F base curve (voltages + stock
        /// frequencies, read-only) with the CURRENT per-point offsets and the
        /// driver-queried per-point editable ranges. Fails with
        /// ControlUnsupported when the driver refuses the curve queries.
        ///
        /// NVAPI-only in practice: a vendor without a per-point boost table
        /// declines this and its write partner together.
        /// </summary>
        GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve();

        /// <summary>
        /// Writes per-point clock offsets (MHz, point order) for the graphics
        /// domain by rewriting the boost table; other domains are preserved
        /// from the driver's current state. Every point is clamped to its
        /// driver-queried range before writing. Count must match the curve
        /// from <see cref="ReadVoltageFrequencyCurve"/>.
        /// </summary>
        GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz);

        /// <summary>
        /// Current per-point graphics-domain offsets from the boost table -
        /// the revert anchor / resync source for curve batches. Empty when
        /// the curve is unsupported.
        /// </summary>
        GpuResult<int[]> ReadVfCurveOffsets();

        /// <summary>
        /// Current voltage-boost percent, or ControlUnsupported when the
        /// driver refuses the query (the normal case on Ampere/Ada - the
        /// vBIOS locks raw voltage control there).
        /// </summary>
        GpuResult<uint> ReadVoltageBoostPercent();

        /// <summary>
        /// Sets voltage-boost percent (0-100). Refused on GPUs where the
        /// vBIOS locks voltage control - surfaced as ControlUnsupported or
        /// WriteRejected, never thrown.
        /// </summary>
        GpuResult SetVoltageBoostPercent(uint percent);
    }
}
