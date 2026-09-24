using System;
using System.Collections.Generic;

namespace kaliteConfig.ProcessOptimizer.Models;

/// <summary>
/// How hard the session optimizer demotes a contended background process.
/// Every level leaves the process's PRIORITY CLASS alone; they differ in how
/// far memory, I/O and per-thread memory are demoted. See
/// <see cref="Services.BackgroundThrottleService.ApplyThrottle"/>.
///
/// There is no Aggressive level. It used to add a per-process Job Object CPU
/// rate cap (which cannot be lifted off a process mid-session - jobs are not
/// escapable, so the cap outlived the session) and an affinity fallback that
/// pinned the process to the highest logical processor, which on a machine
/// with kernel-reserved CPU Sets is a processor the kernel never schedules user
/// threads on. Both were removed in favour of doing less.
/// </summary>
public enum AggressivenessLevel
{
    /// <summary>EcoQoS + memory priority 2 + I/O priority VeryLow.</summary>
    Light,
    /// <summary>Adds memory priority 1 and per-thread memory demotion.</summary>
    Moderate,
}

public class ProcessBaselineSnapshot
{
    public int Pid { get; set; }
    public DateTime ProcessStartTime { get; set; }
    public int OriginalPriorityClass { get; set; }
    public bool OriginalEcoQos { get; set; }
    /// <summary>Null when unreadable - restore skips instead of guessing.</summary>
    public int? OriginalMemoryPriority { get; set; }
    /// <summary>Null when unreadable - restore skips instead of guessing.</summary>
    public int? OriginalIoPriority { get; set; }
    /// <summary>Null when unreadable (e.g. boost never applied) - restore skips.</summary>
    public bool? OriginalBoostDisabled { get; set; }
    public IntPtr OriginalProcessorAffinity { get; set; }
    /// <summary>
    /// Default CPU Sets at capture. Empty/null means "was unrestricted" - the
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
    public string ProcessName { get; set; } = string.Empty;
    public DateTime ManagedSince { get; set; }
    public string ActionTaken { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double LastContentionSignal { get; set; }
    public AggressivenessLevel CurrentThrottleLevel { get; set; }

    /// <summary>
    /// Consecutive samples this process has been calm for. Reset the instant it is
    /// flagged hot again, and the input that decides when it is released - see
    /// <see cref="Services.ContentionPolicy.ShouldRelease"/>. Without it a miss on
    /// a single sample was enough to restore, which is what made a process sitting
    /// near the threshold thrash between demoted and restored.
    /// </summary>
    public int QuietTicks { get; set; }
}

/// <summary>
/// One contention tick. Carries the hot processes AND the full alive set, because
/// the hysteresis counters are keyed by PID: without the alive set a process that
/// exits leaves its count behind, and a later process reusing that PID would
/// inherit it and be demoted on its first busy sample - exactly the "never punish
/// the first sample" guarantee the counters exist to provide.
/// </summary>
public sealed class ContentionSample
{
    public ContentionSample(
        IReadOnlyDictionary<int, double> hotCpuPercent,
        IReadOnlySet<int> alivePids)
    {
        HotCpuPercent = hotCpuPercent;
        AlivePids = alivePids;
    }

    /// <summary>PID to usage as a percentage of total machine CPU, above the bar only.</summary>
    public IReadOnlyDictionary<int, double> HotCpuPercent { get; }

    /// <summary>Every PID seen in this tick's snapshot, hot or not.</summary>
    public IReadOnlySet<int> AlivePids { get; }
}

public class OptimizationSessionState
{
    public bool IsActive { get; set; }
    /// <summary>Null until a session names its game; readers fall back to "None".</summary>
    public string? ActiveGameName { get; set; }
    public int ActiveGamePid { get; set; }
    /// <summary>Null until a session names its profile; readers fall back to "Default".</summary>
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
