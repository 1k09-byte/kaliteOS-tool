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
    public sealed record GpuTelemetrySnapshot
    {
        public required DateTime Timestamp { get; init; }

        public double? CoreClockMHz { get; init; }

        public double? MemClockMHz { get; init; }

        public int? GpuTempC { get; init; }

        public int? HotspotTempC { get; init; }

        public int? MemTempC { get; init; }

        public double? VoltageMv { get; init; }

        public double? PowerDrawW { get; init; }

        public double? PowerDrawPercentOfLimit { get; init; }

        public int? FanRpm { get; init; }

        public int? FanPercent { get; init; }

        public int? GpuUsagePercent { get; init; }

        public double? VramUsageMb { get; init; }

        public int? PcieGen { get; init; }

        public int? PcieWidth { get; init; }

        public double? PcieTxKBs { get; init; }

        public double? PcieRxKBs { get; init; }
    }
}
