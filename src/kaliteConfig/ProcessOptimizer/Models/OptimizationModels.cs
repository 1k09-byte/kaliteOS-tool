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
using System.Collections.Generic;

namespace kaliteConfig.ProcessOptimizer.Models;

public enum AggressivenessLevel
{
    Light,
    Moderate,
}

public class ProcessBaselineSnapshot
{
    public int Pid { get; set; }
    public DateTime ProcessStartTime { get; set; }
    public int OriginalPriorityClass { get; set; }
    public bool OriginalEcoQos { get; set; }
    public int? OriginalMemoryPriority { get; set; }
    public int? OriginalIoPriority { get; set; }
    public bool? OriginalBoostDisabled { get; set; }
    public IntPtr OriginalProcessorAffinity { get; set; }
    public uint[] OriginalCpuSets { get; set; } = Array.Empty<uint>();
}

public sealed class GameThreadSnapshot
{
    public uint Tid { get; set; }
    public int Priority { get; set; }
    public bool BoostDisabled { get; set; }
    public bool Eco { get; set; }
    public uint MemoryPriority { get; set; }
    public ushort IdealGroup { get; set; }
    public byte IdealNumber { get; set; }
    public uint[] CpuSets { get; set; } = Array.Empty<uint>();
}

public class ManagedProcessEntry
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public DateTime ManagedSince { get; set; }
    public string ActionTaken { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double LastContentionSignal { get; set; }
    public AggressivenessLevel CurrentThrottleLevel { get; set; }

    public int QuietTicks { get; set; }
}

public sealed class ContentionSample
{
    public ContentionSample(
        IReadOnlyDictionary<int, double> hotCpuPercent,
        IReadOnlySet<int> alivePids)
    {
        HotCpuPercent = hotCpuPercent;
        AlivePids = alivePids;
    }

    public IReadOnlyDictionary<int, double> HotCpuPercent { get; }

    public IReadOnlySet<int> AlivePids { get; }
}

public class OptimizationSessionState
{
    public bool IsActive { get; set; }
    public string? ActiveGameName { get; set; }
    public int ActiveGamePid { get; set; }
    public string? ProfileName { get; set; }
    public DateTime SessionStartTime { get; set; }
    public int ProcessesManaged { get; set; }
    public int ProcessesThrottled { get; set; }
    public int ProcessesUnchanged { get; set; }
}

public class OptimizationProfile
{
    public AggressivenessLevel Aggressiveness { get; set; } = AggressivenessLevel.Light;
    public List<string> Exclusions { get; set; } = new();
}
