using System;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>A clamped range for one control, in UI units.</summary>
    public sealed record OverclockControlRange(double Minimum, double Maximum, double Step)
    {
        public double Clamp(double value) => Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
    }

    /// <summary>
    /// Per-control ranges queried from the driver at detection time — never
    /// hardcoded. Units reflect what the driver reports: clock offsets in MHz
    /// (kHz on the wire), power limit in percent (PCM on the wire), temp limit
    /// in °C or null when the GPU doesn't support a user temp limit.
    /// </summary>
    public sealed record GpuCapabilities
    {
        public required string GpuName { get; init; }
        public required string DriverVersion { get; init; }
        public bool IsNotebook { get; init; }

        /// <summary>Null when the P0 core clock delta range is missing/read-only.</summary>
        public OverclockControlRange? CoreOffsetRangeMHz { get; init; }

        /// <summary>Null when the P0 memory clock delta range is missing/read-only.</summary>
        public OverclockControlRange? MemOffsetRangeMHz { get; init; }

        /// <summary>Power limit range in percent of default (driver-reported via PCM).</summary>
        public OverclockControlRange? PowerLimitRangePercent { get; init; }

        /// <summary>
        /// Temp limit range in °C, or null when the GPU/driver doesn't expose a
        /// user-adjustable thermal limit — the UI must not render the control then.
        /// </summary>
        public OverclockControlRange? TempLimitRangeC { get; init; }

        /// <summary>Current power target in % at detection time (revert anchor).</summary>
        public double? CurrentPowerLimitPercent { get; init; }

        /// <summary>Current temp limit in °C at detection time (revert anchor).</summary>
        public int? CurrentTempLimitC { get; init; }

        /// <summary>True when at least one cooler is software-controllable.</summary>
        public bool FanControlSupported { get; init; }

        public bool AnyControlSupported
            => CoreOffsetRangeMHz is not null || MemOffsetRangeMHz is not null
               || PowerLimitRangePercent is not null || TempLimitRangeC is not null
               || FanControlSupported;
    }
}
