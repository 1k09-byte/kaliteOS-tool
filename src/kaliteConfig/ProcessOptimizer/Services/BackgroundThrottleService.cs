using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class BackgroundThrottleService
{
    private static readonly ConcurrentDictionary<string, uint> _threadMemoryOriginals = new();

    public static string ApplyThrottle(int pid, AggressivenessLevel level)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);

            if (process.IsInvalid)
                return "Access Denied";

            string actionTaken = "no change";

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

                try
                {
                    var memLow = new ProcessMemoryPriorityInfo { MemoryPriority = 2 };
                    NativeMethods.Power.SetProcessInformation(
                        process, ProcessInformationClass.ProcessMemoryPriority,
                        ref memLow, NativeMethods.Power.MemoryPrioritySize());
                }
                catch { }

                try
                {
                    uint ioLow = 0;
                    NativeMethods.Ntdll.NtSetInformationProcess(
                        process, NativeMethods.Ntdll.ProcessIoPriority,
                        ref ioLow, sizeof(uint));
                }
                catch { }

                actionTaken = "EcoQoS + Mem2 + IOLow (priority unchanged)";
            }

            if (level >= AggressivenessLevel.Moderate)
            {
                var memPriority = new ProcessMemoryPriorityInfo { MemoryPriority = 1 };
                NativeMethods.Power.SetProcessInformation(
                    process, ProcessInformationClass.ProcessMemoryPriority,
                    ref memPriority, NativeMethods.Power.MemoryPrioritySize());

                uint ioPriority = 0;
                NativeMethods.Ntdll.NtSetInformationProcess(
                    process, NativeMethods.Ntdll.ProcessIoPriority,
                    ref ioPriority, sizeof(uint));

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

    public static void RestoreThreadMemory(int pid, long startTicks)
    {
        try
        {
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
