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
using System.Collections.Concurrent;
using System.Diagnostics;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

/// <summary>
/// Demotes a background process's MEMORY and DISK worthiness so the game wins
/// those resources, without ever fighting it for the CPU:
///
/// - the priority CLASS is left alone at every level. Demoting it makes a
///   process compete rather than yield, and the game's 1% lows suffer when a
///   shared dependency (audio, a launcher helper, a driver worker) is starved;
/// - nothing is pinned to a core subset. An earlier build partitioned CPU Sets
///   here, and on a homogeneous CPU that collapsed every background process
///   onto core 0 - the DPC/interrupt core - while the game lost half of the
///   machine. Both are gone; see Docs/GameMode.md.
///
/// What is left scales with <see cref="AggressivenessLevel"/>: EcoQoS, memory
/// priority, I/O priority, and (at Moderate) per-thread memory priority.
/// </summary>
public static class BackgroundThrottleService
{
    /// <summary>
    /// Original per-thread memory priorities demoted by <see cref="DemoteThreadsMemory"/>.
    /// Keyed "pid_startTicks_tid" so PID reuse can never restore into the wrong process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, uint> _threadMemoryOriginals = new();

    /// <summary>
    /// Applies the constraints on a target PID corresponding to the given aggressiveness level.
    /// Returns a string describing the action taken.
    /// </summary>
    public static string ApplyThrottle(int pid, AggressivenessLevel level)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);

            if (process.IsInvalid)
                return "Access Denied";

            string actionTaken = "no change";

            // Light: EcoQoS + memory/IO priority demotion - priority class
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

            // Moderate: Add memory-priority demotion (eco hint already applied above).
            // This is the ceiling - there is no Aggressive tier. The level it used
            // to reach (a per-process Job Object CPU rate cap, and an affinity
            // fallback that on a non-hybrid CPU pinned the process to the LAST
            // logical processor, which can be a kernel-reserved one it may then
            // never run on) did more harm than the contention it removed.
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
}
