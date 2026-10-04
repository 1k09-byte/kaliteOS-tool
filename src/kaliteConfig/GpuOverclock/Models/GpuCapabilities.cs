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

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>A clamped range for one control, in UI units.</summary>
    public sealed record OverclockControlRange(double Minimum, double Maximum, double Step)
    {
        public double Clamp(double value) => Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
    }

    /// <summary>
    /// Per-control ranges queried from the driver at detection time - never
    /// hardcoded. Units reflect what the driver reports: clock offsets in MHz
    /// (kHz on the wire), power limit in percent (PCM on the wire), temp limit
    /// in °C or null when the GPU doesn't support a user temp limit.
    /// </summary>
    public sealed record GpuCapabilities
    {
        public required string GpuName { get; init; }
        public required string DriverVersion { get; init; }
        public bool IsNotebook { get; init; }

        /// <summary>
        /// True when this is an integrated adapter (shares system memory with
        /// the CPU) rather than a discrete card. Read from the vendor API, not
        /// inferred from the product name: a discrete Radeon is a Radeon, and
        /// matching on that substring used to lock out real discrete cards.
        ///
        /// NVIDIA's NVML SystemType enum has no integrated member - it reports
        /// Laptop / Desktop / Workstation / Compute / Tegra - so the NVIDIA
        /// controller leaves this false. That is not a blind spot in practice:
        /// NVIDIA ships no integrated graphics part of its own, and the iGPU
        /// alongside an NVIDIA laptop dGPU is an Intel or AMD adapter, which is
        /// detected by whichever controller owns it.
        /// </summary>
        public bool IsIntegrated { get; init; }

        /// <summary>Null when the P0 core clock delta range is missing/read-only.</summary>
        public OverclockControlRange? CoreOffsetRangeMHz { get; init; }

        /// <summary>Null when the P0 memory clock delta range is missing/read-only.</summary>
        public OverclockControlRange? MemOffsetRangeMHz { get; init; }

        /// <summary>Power limit range in percent of default (driver-reported via PCM).</summary>
        public OverclockControlRange? PowerLimitRangePercent { get; init; }

        /// <summary>
        /// Temp limit range in °C, or null when the GPU/driver doesn't expose a
        /// user-adjustable thermal limit - the UI must not render the control then.
        /// </summary>
        public OverclockControlRange? TempLimitRangeC { get; init; }

        /// <summary>Current power target in % at detection time (revert anchor).</summary>
        public double? CurrentPowerLimitPercent { get; init; }

        /// <summary>Current temp limit in °C at detection time (revert anchor).</summary>
        public int? CurrentTempLimitC { get; init; }

        /// <summary>True when at least one cooler is software-controllable.</summary>
        public bool FanControlSupported { get; init; }

        /// <summary>
        /// True when the card can stop its fan at idle. Separate from
        /// <see cref="FanControlSupported"/>: plenty of cards let you force a
        /// fixed speed but offer no Zero RPM (and the reverse), so the toggle
        /// renders only when the driver actually answers the query.
        /// </summary>
        public bool ZeroRpmSupported { get; init; }

        /// <summary>
        /// True when the driver exposed a readable graphics-domain V/F curve
        /// (base voltages + frequencies + per-point offset ranges). False when
        /// the curve queries are refused - the V/F UI must stay hidden then.
        /// </summary>
        public bool VfCurveSupported { get; init; }

        /// <summary>Number of editable V/F curve points (graphics domain).</summary>
        public int VfCurvePointCount { get; init; }

        /// <summary>
        /// True only when the driver actually answers the voltage-boost-percent
        /// query (GetCoreVoltageBoostPercent). Queried live - never assumed
        /// from the GPU generation. On Ampere/Ada the vBIOS locks raw voltage
        /// control, so this is typically false there.
        /// </summary>
        public bool VoltageBoostSupported { get; init; }

        public bool AnyControlSupported
            => CoreOffsetRangeMHz is not null || MemOffsetRangeMHz is not null
               || PowerLimitRangePercent is not null || TempLimitRangeC is not null
               || FanControlSupported || ZeroRpmSupported
               || VfCurveSupported || VoltageBoostSupported;
    }
}
