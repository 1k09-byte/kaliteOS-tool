using System;
using System.Collections.Generic;

namespace kaliteConfig.ProcessOptimizer.Models;

public enum AggressivenessLevel
{
    Light,      // Only Priority drops
    Moderate,   // Priority + EcoQoS + Memory Priority
    Aggressive  // Priority + EcoQoS + Memory Priority + Job Object CPU Caps
}

public class ProcessBaselineSnapshot
{
    public int Pid { get; set; }
    public DateTime ProcessStartTime { get; set; }
    public int OriginalPriorityClass { get; set; }
    public bool OriginalEcoQos { get; set; }
    /// <summary>Null when unreadable — restore skips instead of guessing.</summary>
    public int? OriginalMemoryPriority { get; set; }
    /// <summary>Null when unreadable — restore skips instead of guessing.</summary>
    public int? OriginalIoPriority { get; set; }
    /// <summary>Null when unreadable (e.g. boost never applied) — restore skips.</summary>
    public bool? OriginalBoostDisabled { get; set; }
    public IntPtr OriginalProcessorAffinity { get; set; }
    /// <summary>
    /// Default CPU Sets at capture. Empty/null means "was unrestricted" — the
    /// restore path then re-applies the full system set list.
    /// </summary>
    public uint[] OriginalCpuSets { get; set; } = Array.Empty<uint>();
}

/// <summary>Per-thread original state for threads the booster touches.</summary>
public sealed class GameThreadSnapshot
{
    public uint Tid { get; set; }
    public int Priority { get; set; }
    public bool BoostDisabled { get; set; }
    public bool Eco { get; set; }
    public uint MemoryPriority { get; set; }
    public ushort IdealGroup { get; set; }
    public byte IdealNumber { get; set; }
    /// <summary>Empty = was inheriting (restore clears the explicit selection).</summary>
    public uint[] CpuSets { get; set; } = Array.Empty<uint>();
}

public class ManagedProcessEntry
{
    public int Pid { get; set; }
    public string ProcessName { get; set; }
    public DateTime ManagedSince { get; set; }
    public string ActionTaken { get; set; }
    public string Reason { get; set; }
    public double LastContentionSignal { get; set; }
    public AggressivenessLevel CurrentThrottleLevel { get; set; }
}

public class OptimizationSessionState
{
    public bool IsActive { get; set; }
    public string ActiveGameName { get; set; }
    public int ActiveGamePid { get; set; }
    public string ProfileName { get; set; }
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
