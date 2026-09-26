// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// Canonical control names. Used by the ViewModel when queueing writes and
    /// by SafetyRevertService when reverting - the revert switch matches on
    /// these, so both sides must stay in lockstep.
    /// </summary>
    public static class OcControlNames
    {
        public const string CoreClockOffset = "Core clock offset";
        public const string MemoryClockOffset = "Memory clock offset";
        public const string PowerLimit = "Power limit";
        public const string TemperatureLimit = "Temperature limit";
        public const string FanSpeed = "Fan speed";

        /// <summary>
        /// Per-point V/F curve offsets. The safety revert switch matches on
        /// this - it restores the pre-batch boost-table deltas, not defaults.
        /// </summary>
        public const string VoltageFrequencyCurve = "V/F curve";

        /// <summary>Voltage-boost percent (only on GPUs where the driver answers it).</summary>
        public const string VoltageBoost = "Voltage boost";
    }
}
