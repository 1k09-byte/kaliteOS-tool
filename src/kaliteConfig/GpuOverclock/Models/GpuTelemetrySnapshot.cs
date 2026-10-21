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
