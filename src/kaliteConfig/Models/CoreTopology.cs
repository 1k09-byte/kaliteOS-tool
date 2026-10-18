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
    public sealed class CoreTopology
    {
        public int TotalLogicalCores { get; init; }
        public int PhysicalCores { get; init; }

        public int ReservedCore { get; init; } = 0;

        public System.Collections.Generic.List<ulong> PerformanceCoreMasks { get; init; } = new();

        public System.Collections.Generic.List<ulong> AllPerformanceCoreMasks { get; init; } = new();

        public System.Collections.Generic.List<ulong> EfficiencyCoreMasks { get; init; } = new();

        public System.Collections.Generic.List<ulong> NumaNodeMasks { get; init; } = new();

        public System.Collections.Generic.List<ulong> CoreComplexMasks { get; init; } = new();

        public bool IsHybrid => EfficiencyCoreMasks.Count > 0;
    }
}
