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
namespace kaliteConfig.Models
{
    /// <summary>
    /// Snapshot of the CPU's physical topology, built once from
    /// GetLogicalProcessorInformationEx and cached for the session.
    /// </summary>
    public sealed class CoreTopology
    {
        public int TotalLogicalCores { get; init; }
        public int PhysicalCores { get; init; }

        /// <summary>Always 0 - the OS scheduler's own core is never assigned to devices.</summary>
        public int ReservedCore { get; init; } = 0;

        /// <summary>
        /// Group Affinity bitmasks representing full physical cores (EfficiencyClass == 0).
        /// On non-hybrid CPUs this contains every physical core's mask except the reserved one.
        /// </summary>
        public System.Collections.Generic.List<ulong> PerformanceCoreMasks { get; init; } = new();

        /// <summary>
        /// Every performance-core mask INCLUDING the reserved core 0, in
        /// enumeration order. Used by Optimize, which follows the AutoOS
        /// layout that counts core 0 (its 4-core branch pins audio to it).
        /// </summary>
        public System.Collections.Generic.List<ulong> AllPerformanceCoreMasks { get; init; } = new();

        /// <summary>
        /// Group Affinity bitmasks representing full Efficiency cores (EfficiencyClass > 0).
        /// Empty on non-hybrid CPUs.
        /// </summary>
        public System.Collections.Generic.List<ulong> EfficiencyCoreMasks { get; init; } = new();

        /// <summary>
        /// Group Affinity bitmasks representing distinct NUMA nodes.
        /// </summary>
        public System.Collections.Generic.List<ulong> NumaNodeMasks { get; init; } = new();

        /// <summary>
        /// Group Affinity bitmasks representing distinct L3 Cache domains (Core Complexes / CCX).
        /// </summary>
        public System.Collections.Generic.List<ulong> CoreComplexMasks { get; init; } = new();

        public bool IsHybrid => EfficiencyCoreMasks.Count > 0;
    }
}
