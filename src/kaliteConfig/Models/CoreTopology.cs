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
