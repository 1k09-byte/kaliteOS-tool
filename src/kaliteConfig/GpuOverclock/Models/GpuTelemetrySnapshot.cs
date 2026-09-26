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
    /// <summary>
    /// Immutable telemetry snapshot for one poll tick. Every field is nullable:
    /// a null means "not reported by this GPU/driver for this tick", never zero.
    /// The polling service publishes these; consumers must not mutate them.
    /// </summary>
    public sealed record GpuTelemetrySnapshot
    {
        public required DateTime Timestamp { get; init; }

        /// <summary>Current graphics core clock in MHz (NVAPI graphics clock domain).</summary>
        public double? CoreClockMHz { get; init; }

        /// <summary>
        /// Current memory-domain clock in MHz, in the same unit nvidia-smi reports
        /// (the DDR clock, not the effective transfer rate).
        /// </summary>
        public double? MemClockMHz { get; init; }

        /// <summary>GPU core temperature in °C (first GPU-target thermal sensor).</summary>
        public int? GpuTempC { get; init; }

        /// <summary>
        /// Hotspot temperature in °C when the driver exposes a second GPU-target
        /// sensor. Classic NVAPI often does not expose hotspot on Ada - null then.
        /// </summary>
        public int? HotspotTempC { get; init; }

        /// <summary>
        /// Memory (VRAM) temperature in °C when the driver exposes a
        /// Memory-target thermal sensor. Often absent on consumer cards - null then.
        /// </summary>
        public int? MemTempC { get; init; }

        /// <summary>GPU core voltage in millivolts (private NVAPI API; null if refused).</summary>
        public double? VoltageMv { get; init; }

        /// <summary>
        /// Absolute board power draw in watts. NVAPI reports power draw only as a
        /// percentage of the current power limit (see <see cref="PowerDrawPercentOfLimit"/>);
        /// this stays null until an anchored absolute source (driver nvml.dll) is wired.
        /// </summary>
        public double? PowerDrawW { get; init; }

        /// <summary>Board power draw as % of the currently-set power limit. The value
        /// NVAPI natively provides (power topology status).</summary>
        public double? PowerDrawPercentOfLimit { get; init; }

        /// <summary>Fan speed in RPM (first controllable cooler).</summary>
        public int? FanRpm { get; init; }

        /// <summary>Fan speed as % of maximum (what nvidia-smi calls fan.speed).</summary>
        public int? FanPercent { get; init; }

        /// <summary>GPU (graphics engine) utilization in %.</summary>
        public int? GpuUsagePercent { get; init; }

        /// <summary>Dedicated VRAM in use, in MB (total minus current available).</summary>
        public double? VramUsageMb { get; init; }

        /// <summary>Active PCIe link generation, when the driver reports it.</summary>
        public int? PcieGen { get; init; }

        /// <summary>Active PCIe link width (lanes).</summary>
        public int? PcieWidth { get; init; }

        /// <summary>
        /// PCIe host-to-device throughput in KB/s (NVML cumulative counter
        /// differenced across ticks). Null until two ticks have elapsed or
        /// when NVML refuses the counter.
        /// </summary>
        public double? PcieTxKBs { get; init; }

        /// <summary>PCIe device-to-host throughput in KB/s (same source as <see cref="PcieTxKBs"/>).</summary>
        public double? PcieRxKBs { get; init; }
    }
}
