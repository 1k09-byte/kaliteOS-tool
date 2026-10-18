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
    public sealed record OverclockControlRange(double Minimum, double Maximum, double Step)
    {
        public double Clamp(double value) => Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
    }

    public sealed record GpuCapabilities
    {
        public required string GpuName { get; init; }
        public required string DriverVersion { get; init; }
        public bool IsNotebook { get; init; }

        public bool IsIntegrated { get; init; }

        public OverclockControlRange? CoreOffsetRangeMHz { get; init; }

        public OverclockControlRange? MemOffsetRangeMHz { get; init; }

        public OverclockControlRange? PowerLimitRangePercent { get; init; }

        public OverclockControlRange? TempLimitRangeC { get; init; }

        public double? CurrentPowerLimitPercent { get; init; }

        public int? CurrentTempLimitC { get; init; }

        public bool FanControlSupported { get; init; }

        public bool ZeroRpmSupported { get; init; }

        public bool VfCurveSupported { get; init; }

        public int VfCurvePointCount { get; init; }

        public bool VoltageBoostSupported { get; init; }

        public bool AnyControlSupported
            => CoreOffsetRangeMHz is not null || MemOffsetRangeMHz is not null
               || PowerLimitRangePercent is not null || TempLimitRangeC is not null
               || FanControlSupported || ZeroRpmSupported
               || VfCurveSupported || VoltageBoostSupported;
    }
}
