// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.GpuOverclock.Services
{
    internal static class AmdGpuSimulation
    {
        public const string EnvVar = "KALITE_OC_SIMULATE";

        internal enum SimKind { None, Discrete, PreNavi4, Integrated }

        internal static SimKind Kind =>
            Parse(Environment.GetEnvironmentVariable(EnvVar)?.Trim().ToLowerInvariant());

        internal static bool IsActive => Kind != SimKind.None;

        internal static string Label => Kind switch
        {
            SimKind.Discrete => "Simulated Radeon (Navi4+, offset clocks)",
            SimKind.PreNavi4 => "Simulated Radeon (pre-Navi4, absolute clocks)",
            SimKind.Integrated => "Simulated Radeon (integrated, no controls)",
            _ => "",
        };

        internal static List<string> Writes { get; } = new();

        internal static void Record(string description)
        {
            lock (Writes) { Writes.Add(description); }
        }

        internal static void Reset()
        {
            lock (Writes) { Writes.Clear(); }
            CoreOffsetMhz = 0;
            MemOffsetMhz = 0;
            PowerLimitPercent = 100;
            FanPercent = null;
            ZeroRpm = false;
        }

        private static SimKind Parse(string? value) => value switch
        {
            "amd" or "radeon" or "discrete" => SimKind.Discrete,
            "amd-prenavi4" or "prenavi4" => SimKind.PreNavi4,
            "amd-igpu" or "igpu" => SimKind.Integrated,
            _ => SimKind.None,
        };

        internal static string CardName => Kind == SimKind.Integrated
            ? "AMD Radeon(TM) Graphics (simulated)"
            : "AMD Radeon RX 7900 XTX (simulated)";

        internal static string PnpString => Kind == SimKind.Integrated
            ? @"PCI\VEN_1002&DEV_13C0&SUBSYS_D0001458&REV_C5\4&16012499&0&0041"
            : @"PCI\VEN_1002&DEV_744C&SUBSYS_5316148&REV_CC\4&16012499&0&0042";

        internal static bool IsDiscrete => Kind != SimKind.Integrated;

        internal static uint VramMb => Kind == SimKind.Integrated ? 512u : 24576u;

        internal static AmdGpuTuningSupport Support => new(
            Name: CardName,
            PnpString: PnpString,
            IsDiscrete: IsDiscrete,
            VramMb: VramMb,
            ManualGfx: Kind != SimKind.Integrated,
            ManualVram: Kind != SimKind.Integrated,
            ManualFan: Kind != SimKind.Integrated,
            ManualPower: Kind != SimKind.Integrated);

        internal static AdlxIntRange CoreRange => Kind == SimKind.PreNavi4
            ? new AdlxIntRange { MinValue = 2000, MaxValue = 3000, Step = 10 }
            : new AdlxIntRange { MinValue = -500, MaxValue = 500, Step = 5 };

        internal static AdlxIntRange VramRange => new() { MinValue = 20000, MaxValue = 24000, Step = 50 };

        internal static AdlxIntRange PowerRange => new() { MinValue = 0, MaxValue = 107, Step = 1 };

        internal static AdlxIntRange FanRange => new() { MinValue = 0, MaxValue = 100, Step = 1 };

        internal static int VramBaselineMhz => 20000;
        internal static int PowerBaselinePercent => 100;

        internal static int CoreOffsetMhz { get; set; }
        internal static int MemOffsetMhz { get; set; }
        internal static double PowerLimitPercent { get; set; } = 100;

        internal static int? FanPercent { get; set; }

        internal static bool ZeroRpm { get; set; }

        internal static string RecentWrites(int take = 8)
        {
            lock (Writes) { return string.Join("\n", Writes.TakeLast(take)); }
        }
    }
}
