// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// A fake Radeon, for looking at the AMD page on a machine that has none.
    ///
    /// The development machine's only AMD adapter is an integrated GPU whose
    /// driver reports no manual tuning domains, which means the AMD interface
    /// renders as a dead end - correct, but useless for answering "what does
    /// this look like on a real Radeon?". This fabricates the card that ADLX
    /// would report on a discrete one: a real-looking name, driver-style
    /// ranges, and values that move when you apply them, so the whole page -
    /// sliders, anchors, the safety machine's revert - can be exercised.
    ///
    /// Safety property, and the reason this is a separate type rather than a
    /// flag sprinkled through the controller: a simulated write NEVER reaches a
    /// driver. There is no code path from here to ADLX - the controller either
    /// talks to a real session or to this, never both - so nothing on the
    /// development machine can be overclocked by mistake, and a screenshot of
    /// the simulated page can never be mistaken for a real measurement.
    ///
    /// Enable with an environment variable, which is the module's existing dev
    /// affordance (KALITE_OC_BACKEND does the same for backend selection):
    ///
    ///   KALITE_OC_SIMULATE=amd            discrete Radeon, Navi4+ offset clocks
    ///   KALITE_OC_SIMULATE=amd-prenavi4   same card, but the driver reports
    ///                                     core frequency as an ABSOLUTE value,
    ///                                     so the core control correctly hides
    ///   KALITE_OC_SIMULATE=amd-igpu       the integrated case, no controls
    /// </summary>
    internal static class AmdGpuSimulation
    {
        public const string EnvVar = "KALITE_OC_SIMULATE";

        internal enum SimKind { None, Discrete, PreNavi4, Integrated }

        /// <summary>
        /// Read from the environment on every use rather than cached in a static
        /// initializer. In the app the process environment never changes, so this
        /// is the same answer - but it keeps the flag settable from a test
        /// process, which is the only way the simulation's own behaviour can be
        /// covered at all.
        /// </summary>
        internal static SimKind Kind =>
            Parse(Environment.GetEnvironmentVariable(EnvVar)?.Trim().ToLowerInvariant());

        internal static bool IsActive => Kind != SimKind.None;

        /// <summary>Shown in the UI so a simulated page is never read as a real one.</summary>
        internal static string Label => Kind switch
        {
            SimKind.Discrete => "Simulated Radeon (Navi4+, offset clocks)",
            SimKind.PreNavi4 => "Simulated Radeon (pre-Navi4, absolute clocks)",
            SimKind.Integrated => "Simulated Radeon (integrated, no controls)",
            _ => "",
        };

        /// <summary>Every write that WOULD have gone to a driver, newest last.</summary>
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

        // ---- what the simulated driver reports ---------------------------------

        internal static string CardName => Kind == SimKind.Integrated
            ? "AMD Radeon(TM) Graphics (simulated)"
            : "AMD Radeon RX 7900 XTX (simulated)";

        internal static string PnpString => Kind == SimKind.Integrated
            ? @"PCI\VEN_1002&DEV_13C0&SUBSYS_D0001458&REV_C5\4&16012499&0&0041"
            : @"PCI\VEN_1002&DEV_744C&SUBSYS_5316148&REV_CC\4&16012499&0&0042";

        internal static bool IsDiscrete => Kind != SimKind.Integrated;

        internal static uint VramMb => Kind == SimKind.Integrated ? 512u : 24576u;

        /// <summary>What the capability queries answer. The integrated case says no to all four.</summary>
        internal static AmdGpuTuningSupport Support => new(
            Name: CardName,
            PnpString: PnpString,
            IsDiscrete: IsDiscrete,
            VramMb: VramMb,
            ManualGfx: Kind != SimKind.Integrated,
            ManualVram: Kind != SimKind.Integrated,
            ManualFan: Kind != SimKind.Integrated,
            ManualPower: Kind != SimKind.Integrated);

        // Core: Navi4+ reports an offset range straddling zero. The pre-Navi4
        // profile reports a block of positive absolute frequencies, which is
        // exactly the case the controller must refuse to interpret.
        internal static AdlxIntRange CoreRange => Kind == SimKind.PreNavi4
            ? new AdlxIntRange { MinValue = 2000, MaxValue = 3000, Step = 10 }
            : new AdlxIntRange { MinValue = -500, MaxValue = 500, Step = 5 };

        // VRAM is always absolute: 20 Gbps of GDDR6 expressed as 20000 MHz.
        internal static AdlxIntRange VramRange => new() { MinValue = 20000, MaxValue = 24000, Step = 50 };

        // ADLX reports the power limit in percent, 100 being stock.
        internal static AdlxIntRange PowerRange => new() { MinValue = 0, MaxValue = 107, Step = 1 };

        internal static AdlxIntRange FanRange => new() { MinValue = 0, MaxValue = 100, Step = 1 };

        internal static int VramBaselineMhz => 20000;
        internal static int PowerBaselinePercent => 100;

        // ---- the values that move when a change is applied ---------------------

        internal static int CoreOffsetMhz { get; set; }
        internal static int MemOffsetMhz { get; set; }
        internal static double PowerLimitPercent { get; set; } = 100;

        /// <summary>Null means "the driver owns the fan" - no manual speed set.</summary>
        internal static int? FanPercent { get; set; }

        internal static bool ZeroRpm { get; set; }

        /// <summary>What a simulated write looks like in the change log.</summary>
        internal static string RecentWrites(int take = 8)
        {
            lock (Writes) { return string.Join("\n", Writes.TakeLast(take)); }
        }
    }
}
