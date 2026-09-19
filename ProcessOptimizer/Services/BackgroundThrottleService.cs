using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class BackgroundThrottleService
{
    /// <summary>
    /// Original per-thread memory priorities demoted by <see cref="DemoteThreadsMemory"/>.
    /// Keyed "pid_startTicks_tid" so PID reuse can never restore into the wrong process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, uint> _threadMemoryOriginals = new();
    private static readonly Microsoft.Win32.SafeHandles.SafeFileHandle _globalJob;

    static BackgroundThrottleService()
    {
        _globalJob = NativeMethods.JobObjects.CreateJobObjectW(IntPtr.Zero, null);
        if (!_globalJob.IsInvalid)
        {
            var limit = new NativeMethods.JobObjects.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = NativeMethods.JobObjects.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | 
                               NativeMethods.JobObjects.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = 2000 // 20% CPU time (2% starves shared dependencies -> 1% low collapse)
            };
            NativeMethods.JobObjects.SetInformationJobObject(
                _globalJob,
                NativeMethods.JobObjects.JobObjectCpuRateControlInformation,
                ref limit,
                8);
        }
    }

    /// <summary>
    /// Temporarily lifts the global Job Object limits when the session ends.
    /// Processes cannot be removed from Jobs, but the limits can be turned off!
    /// </summary>
    public static void LiftGlobalJobLimits()
    {
        if (_globalJob == null || _globalJob.IsInvalid) return;

        var limit = new NativeMethods.JobObjects.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = 0, // Disabled
            CpuRate = 0
        };
        NativeMethods.JobObjects.SetInformationJobObject(
            _globalJob,
            NativeMethods.JobObjects.JobObjectCpuRateControlInformation,
            ref limit,
            8);
    }

    /// <summary>
    /// Affinity-mask fallback when CPU Sets are unavailable: all logical
    /// processors EXCEPT the game partition's (game sets ∪ kernel-reserved).
    /// 0 = no partition active / nothing sensible to compute.
    /// </summary>
    private static ulong ComputeBackgroundComplementMask()
    {
        try
        {
            var game = CpuSetPartitionService.LastGameSets;
            if (game is not { Length: > 0 }) return 0;
            var reserved = CpuSetPartitionService.LastReservedSets ?? Array.Empty<uint>();

            var topo = CpuSetPartitionService.QueryTopologyPublic();
            ulong all = 0, exclude = 0;
            foreach (var e in topo)
            {
                if (e.LogicalIndex >= 64) continue;
                all |= 1UL << e.LogicalIndex;
                if (game.Contains(e.Id) || reserved.Contains(e.Id))
                    exclude |= 1UL << e.LogicalIndex;
            }
            ulong bg = all & ~exclude;
            return bg;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Applies constraints on a target PID corresponding to the given aggressiveness level.
    /// Returns a string describing the action taken.
    /// </summary>
    public static string ApplyThrottle(int pid, AggressivenessLevel level)
    {
        string actionTaken = "";

        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);

            if (process.IsInvalid)
                return "Access Denied";

            // Keep throttled background off the game CCX when a partition is active.
            var bgSets = CpuSetPartitionService.GetBackgroundSets();
            if (bgSets != null && bgSets.Length > 0)
            {
                try { NativeMethods.CpuSets.SetProcessDefaultCpuSets(process, bgSets, (uint)bgSets.Length); } catch { }
            }
            else
            {
                // Sets unsupported (or no session): fall back to affinity —
                // mask the throttled process off the game's cores.
                ulong bgMask = ComputeBackgroundComplementMask();
                if (bgMask != 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        proc.ProcessorAffinity = (IntPtr)(long)bgMask;
                    }
                    catch { }
                }
            }

            // Light: EcoQoS + memory/IO priority demotion — priority class
            // stays Normal. The demotions make background pages trim first
            // and yield disk bandwidth to the game without scheduler churn.
            if (level >= AggressivenessLevel.Light)
            {
                var state = new ProcessPowerThrottlingState
                {
                    Version = NativeMethods.Power.Version,
                    ControlMask = NativeMethods.Power.ExecutionSpeed,
                    StateMask = NativeMethods.Power.ExecutionSpeed,
                };
                NativeMethods.Power.SetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize());

                // Memory priority 2: background pages get reclaimed before the
                // game's (game stays at default 5).
                try
                {
                    var memLow = new ProcessMemoryPriorityInfo { MemoryPriority = 2 };
                    NativeMethods.Power.SetProcessInformation(
                        process, ProcessInformationClass.ProcessMemoryPriority,
                        ref memLow, NativeMethods.Power.MemoryPrioritySize());
                }
                catch { }

                // IO priority VeryLow: background reads/writes yield to the
                // game's streaming and asset loads.
                try
                {
                    uint ioLow = 0; // VeryLow
                    NativeMethods.Ntdll.NtSetInformationProcess(
                        process, NativeMethods.Ntdll.ProcessIoPriority,
                        ref ioLow, sizeof(uint));
                }
                catch { }

                actionTaken = "EcoQoS + Mem2 + IOLow (priority unchanged)";
            }

            // Moderate: Add memory-priority demotion (eco hint already applied above)
            if (level >= AggressivenessLevel.Moderate)
            {
                var memPriority = new ProcessMemoryPriorityInfo { MemoryPriority = 1 };
                NativeMethods.Power.SetProcessInformation(
                    process, ProcessInformationClass.ProcessMemoryPriority,
                    ref memPriority, NativeMethods.Power.MemoryPrioritySize());

                uint ioPriority = 0; // Very Low
                NativeMethods.Ntdll.NtSetInformationProcess(
                    process, NativeMethods.Ntdll.ProcessIoPriority,
                    ref ioPriority, sizeof(uint));

                // Thread tiering: every thread of a demoted process drops to
                // memory priority 1 so the game (5) always wins reclamation.
                DemoteThreadsMemory(pid);

                actionTaken = "EcoQoS + Low I/O + Mem1 (priority unchanged)";
            }

            // Aggressive: Add Process Priority Boost Disable and Job Object Limits
            if (level >= AggressivenessLevel.Aggressive)
            {                    // Fix 1: Disable priority boost on a per-suppressed-process basis to prevent OS scheduling micro-spikes
                    NativeMethods.Priority.SetProcessPriorityBoost(process, true);

                // Fix 4: Job Object CPU Rate Limiting with Fallback
                bool jobAssigned = false;
                if (_globalJob != null && !_globalJob.IsInvalid)
                {
                    jobAssigned = NativeMethods.JobObjects.AssignProcessToJobObject(_globalJob, process);
                    if (jobAssigned)
                    {
                        actionTaken = "Priority: Idle + Eco + Job CPU Cap 20%";
                    }
                }

                if (!jobAssigned)
                {
                    // Fix 4 Fallback: pin to efficiency / last cores, never Core 0
                    // (Core 0 hosts DWM/interrupts; colliding with the game causes hitches).
                    actionTaken = "Priority: Idle + Eco + Low I/O (Job Failed)";

                    try
                    {
                        int mask = GetEfficiencyFallbackAffinity();
                        using var proc = Process.GetProcessById(pid);
                        proc.ProcessorAffinity = (IntPtr)mask;
                        actionTaken = "Priority: Idle + Eco + Affinity: E-cores (Job Failed)";
                    }
                    catch { }
                }
            }

            return actionTaken;
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Best-effort: drop all threads of pid to memory priority 1, recording
    /// each thread's original level first so restore is exact.
    /// </summary>
    private static void DemoteThreadsMemory(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            long ticks;
            try { ticks = proc.StartTime.Ticks; } catch { return; }
            uint memSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ThreadMemoryPriorityInfo>();
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                uint tid = (uint)t.Id;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                        false, tid);
                    if (hThread.IsInvalid) continue;
                    var current = new ThreadMemoryPriorityInfo();
                    if (NativeMethods.Power.GetThreadInformation(
                            hThread, ThreadInformationClass.ThreadMemoryPriority,
                            ref current, memSize))
                    {
                        _threadMemoryOriginals[$"{pid}_{ticks}_{tid}"] = current.MemoryPriority;
                    }
                    var mem = new ThreadMemoryPriorityInfo { MemoryPriority = 1 };
                    NativeMethods.Power.SetThreadInformation(
                        hThread, ThreadInformationClass.ThreadMemoryPriority,
                        ref mem, memSize);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Restores thread memory priorities demoted by <see cref="DemoteThreadsMemory"/>
    /// for the exact process instance (start-time guarded). Called from the
    /// snapshot restore path; safe to call for processes never demoted.
    /// </summary>
    public static void RestoreThreadMemory(int pid, long startTicks)
    {
        try
        {
            // PID-reuse guard: only restore into the exact same instance.
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited || proc.StartTime.Ticks != startTicks) return;
            }
            catch { return; }

            ScrubDeadEntries();
            string prefix = $"{pid}_{startTicks}_";
            uint memSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ThreadMemoryPriorityInfo>();
            foreach (var kvp in _threadMemoryOriginals)
            {
                if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!uint.TryParse(kvp.Key.Substring(prefix.Length), out uint tid)) continue;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    var mem = new ThreadMemoryPriorityInfo { MemoryPriority = kvp.Value };
                    if (NativeMethods.Power.SetThreadInformation(
                            hThread, ThreadInformationClass.ThreadMemoryPriority,
                            ref mem, memSize))
                    {
                        _threadMemoryOriginals.TryRemove(kvp.Key, out _);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Drops ledger entries for processes that no longer exist (exited
    /// mid-session without a restore pass) so the static map can't grow
    /// across sessions.
    /// </summary>
    private static void ScrubDeadEntries()
    {
        try
        {
            foreach (string key in _threadMemoryOriginals.Keys)
            {
                int first = key.IndexOf('_');
                int second = first < 0 ? -1 : key.IndexOf('_', first + 1);
                if (first < 0 || second < 0) continue;
                if (!int.TryParse(key.Substring(0, first), out int pid)) continue;
                if (!long.TryParse(key.Substring(first + 1, second - first - 1), out long ticks)) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (proc.HasExited || proc.StartTime.Ticks != ticks)
                        _threadMemoryOriginals.TryRemove(key, out _);
                }
                catch
                {
                    _threadMemoryOriginals.TryRemove(key, out _);
                }
            }
        }
        catch { }
    }

    private static int GetEfficiencyFallbackAffinity()
    {
        try
        {
            var topo = kaliteConfig.Services.TopologyService.Get();
            int mask = 0;
            foreach (var m in topo.EfficiencyCoreMasks)
                mask |= (int)m;
            if (mask != 0) return mask;
            // Last resort: highest core only (avoids Core 0 / DWM).
            int count = System.Environment.ProcessorCount;
            return count > 1 ? (1 << (count - 1)) : 1;
        }
        catch { return 2; }
    }
}
