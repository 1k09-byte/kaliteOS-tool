using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ProcessStateSnapshotService
{
    /// <summary>
    /// Snapshots everything game mode / the booster / the throttle can change
    /// on a process: priority, boost flag, EcoQoS, memory priority, IO
    /// priority, affinity, and default CPU Sets. Reads are best-effort and
    /// individually nullable — restore skips what could not be read instead
    /// of writing assumed defaults. Excludes protected processes.
    /// </summary>
    public static ProcessBaselineSnapshot CaptureSnapshot(int pid, IEnumerable<string> customExclusions = null)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc == null || proc.HasExited) return null;

            string name = proc.ProcessName + ".exe";
            if (ProtectedProcessGuard.IsProcessProtected(name, customExclusions))
            {
                return null;
            }

            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);

            if (process.IsInvalid) return null;

            var snapshot = new ProcessBaselineSnapshot
            {
                Pid = pid,
                ProcessStartTime = proc.StartTime,
            };

            try { snapshot.OriginalProcessorAffinity = proc.ProcessorAffinity; }
            catch { snapshot.OriginalProcessorAffinity = IntPtr.Zero; }

            // Priority Class
            snapshot.OriginalPriorityClass = (int)NativeMethods.Priority.GetPriorityClass(process);

            // EcoQoS
            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                snapshot.OriginalEcoQos = (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
            }

            // Priority boost flag (Aggressive throttle disables it; booster enables it).
            try
            {
                if (NativeMethods.Priority.GetProcessPriorityBoost(process, out bool disabled))
                    snapshot.OriginalBoostDisabled = disabled;
            }
            catch { }

            // Memory priority (real read; previously assumed 5 and restored
            // wrong values for processes that never ran at Normal).
            try
            {
                var mem = new ProcessMemoryPriorityInfo();
                if (NativeMethods.Power.GetProcessInformation(
                        process, ProcessInformationClass.ProcessMemoryPriority,
                        ref mem, NativeMethods.Power.MemoryPrioritySize()))
                {
                    snapshot.OriginalMemoryPriority = (int)mem.MemoryPriority;
                }
            }
            catch { }

            // IO priority (real read via NtQueryInformationProcess, class 33).
            try
            {
                uint io = 0;
                if (NativeMethods.Ntdll.NtQueryInformationProcess(
                        process, NativeMethods.Ntdll.ProcessIoPriority,
                        ref io, sizeof(uint), out _) == 0)
                {
                    snapshot.OriginalIoPriority = (int)io;
                }
            }
            catch { }

            // Default CPU Sets. Nothing in Game Mode writes these any more (the
            // automatic partition is gone — Docs/GameMode.md); they are still
            // captured and restored because a per-rule CPU-set change lives on
            // the same process and must survive a session either way.
            try
            {
                uint[]? sets = ReadProcessCpuSets(process);
                snapshot.OriginalCpuSets = sets is { Length: > 0 } ? sets : ReadSystemCpuSetIds();
            }
            catch { snapshot.OriginalCpuSets = Array.Empty<uint>(); }

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restores the process exactly to the provided snapshot, assuming PID and
    /// StartTime still match. Restores priority, boost, Eco, memory, IO,
    /// affinity, and default CPU Sets, plus background thread-memory originals.
    /// </summary>
    public static void RestoreSnapshot(ProcessBaselineSnapshot snapshot)
    {
        if (snapshot == null) return;
        try
        {
            using var proc = Process.GetProcessById(snapshot.Pid);
            // Protect against PID reuse: ensure the process we're restoring is the exact same session instance
            if (proc.HasExited || proc.StartTime != snapshot.ProcessStartTime) return;

            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)snapshot.Pid);

            if (process.IsInvalid) return;

            // 1. Restore Priority Class
            if (snapshot.OriginalPriorityClass != 0)
            {
                NativeMethods.Priority.SetPriorityClass(process, (uint)snapshot.OriginalPriorityClass);
            }

            // 2. Restore priority boost flag (throttle may have disabled it).
            if (snapshot.OriginalBoostDisabled.HasValue)
            {
                try { NativeMethods.Priority.SetProcessPriorityBoost(process, snapshot.OriginalBoostDisabled.Value); }
                catch { }
            }

            // 3. Restore EcoQoS
            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = snapshot.OriginalEcoQos ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            NativeMethods.Power.SetProcessInformation(
                process, ProcessInformationClass.ProcessPowerThrottling,
                ref state, NativeMethods.Power.StateSize());

            // 4. Restore Memory Priority (only what was actually read).
            if (snapshot.OriginalMemoryPriority.HasValue)
            {
                var memPriority = new ProcessMemoryPriorityInfo
                {
                    MemoryPriority = (uint)snapshot.OriginalMemoryPriority.Value
                };
                NativeMethods.Power.SetProcessInformation(
                    process, ProcessInformationClass.ProcessMemoryPriority,
                    ref memPriority, NativeMethods.Power.MemoryPrioritySize());
            }

            // 5. Restore IO Priority (only what was actually read).
            if (snapshot.OriginalIoPriority.HasValue)
            {
                uint ioPriority = (uint)snapshot.OriginalIoPriority.Value;
                NativeMethods.Ntdll.NtSetInformationProcess(
                    process, NativeMethods.Ntdll.ProcessIoPriority,
                    ref ioPriority, sizeof(uint));
            }

            // 6. Restore Affinity. Game Mode no longer writes affinity, so this
            // is a no-op for its own changes and stays for anything else that
            // moved the process while the session was open.
            if (snapshot.OriginalProcessorAffinity != IntPtr.Zero)
            {
                try { proc.ProcessorAffinity = snapshot.OriginalProcessorAffinity; }
                catch { }
            }

            // 7. Restore default CPU Sets (see the capture note above).
            if (snapshot.OriginalCpuSets is { Length: > 0 })
            {
                try
                {
                    NativeMethods.CpuSets.SetProcessDefaultCpuSets(
                        process, snapshot.OriginalCpuSets, (uint)snapshot.OriginalCpuSets.Length);
                }
                catch { }
            }

            // 8. Restore background thread-memory originals demoted by the throttle.
            BackgroundThrottleService.RestoreThreadMemory(snapshot.Pid, snapshot.ProcessStartTime.Ticks);
        }
        catch
        {
            // Process exited mid-restore or access denied
        }
    }

    /// <summary>
    /// Snapshots live threads of the game process BEFORE the booster rewrites
    /// them (priority, boost, Eco, memory priority, ideal processor, selected
    /// CPU Sets). Threads born mid-session have no baseline and are left alone
    /// on restore — they die with the game anyway.
    /// </summary>
    public static (long StartTicks, List<GameThreadSnapshot> Threads) CaptureThreadSnapshots(int pid)
    {
        var list = new List<GameThreadSnapshot>();
        long ticks = 0;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return (0, list);
            ticks = proc.StartTime.Ticks;

            uint memSize = (uint)Marshal.SizeOf<ThreadMemoryPriorityInfo>();
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                uint tid = (uint)t.Id;
                try
                {
                    using var thread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                        false, tid);
                    if (thread.IsInvalid) continue;

                    var snap = new GameThreadSnapshot { Tid = tid };
                    snap.Priority = NativeMethods.Priority.GetThreadPriority(thread);

                    if (NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool disabled))
                        snap.BoostDisabled = disabled;

                    var eco = new ThreadPowerThrottlingState { Version = NativeMethods.Power.ThreadVersion };
                    if (NativeMethods.Power.GetThreadInformation(
                            thread, ThreadInformationClass.ThreadPowerThrottling,
                            ref eco, NativeMethods.Power.ThreadStateSize()))
                    {
                        snap.Eco = (eco.StateMask & NativeMethods.Power.ThreadExecutionSpeed) != 0;
                    }

                    var mem = new ThreadMemoryPriorityInfo();
                    if (NativeMethods.Power.GetThreadInformation(
                            thread, ThreadInformationClass.ThreadMemoryPriority,
                            ref mem, memSize))
                    {
                        snap.MemoryPriority = mem.MemoryPriority;
                    }
                    else snap.MemoryPriority = 5;

                    try
                    {
                        // BOOL return: the out parameter is only meaningful when
                        // the call succeeded (a failed read used to be recorded
                        // as a real ideal processor).
                        if (NativeMethods.Affinity.GetThreadIdealProcessorEx(thread, out var ideal))
                        {
                            snap.IdealGroup = ideal.Group;
                            snap.IdealNumber = ideal.Number;
                        }
                        else
                        {
                            snap.IdealGroup = 0;
                            snap.IdealNumber = 0;
                        }
                    }
                    catch { snap.IdealGroup = 0; snap.IdealNumber = 0; }

                    try { snap.CpuSets = ReadThreadCpuSets(thread) ?? Array.Empty<uint>(); }
                    catch { snap.CpuSets = Array.Empty<uint>(); }

                    list.Add(snap);
                }
                catch { }
            }
        }
        catch { }
        return (ticks, list);
    }

    /// <summary>Restores game threads captured by <see cref="CaptureThreadSnapshots"/>.</summary>
    public static void RestoreThreadSnapshots(int pid, long startTicks, List<GameThreadSnapshot> snapshots)
    {
        if (snapshots == null || snapshots.Count == 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;
            long nowTicks;
            try { nowTicks = proc.StartTime.Ticks; } catch { return; }
            if (startTicks != 0 && nowTicks != startTicks) return; // PID reused

            uint memSize = (uint)Marshal.SizeOf<ThreadMemoryPriorityInfo>();
            foreach (var snap in snapshots)
            {
                try
                {
                    using var thread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                        false, snap.Tid);
                    if (thread.IsInvalid) continue; // thread exited mid-session

                    NativeMethods.Priority.SetThreadPriority(thread, snap.Priority);
                    try { NativeMethods.Priority.SetThreadPriorityBoost(thread, snap.BoostDisabled); }
                    catch { }

                    var eco = new ThreadPowerThrottlingState
                    {
                        Version = NativeMethods.Power.ThreadVersion,
                        ControlMask = NativeMethods.Power.ThreadExecutionSpeed,
                        StateMask = snap.Eco ? NativeMethods.Power.ThreadExecutionSpeed : 0,
                    };
                    try
                    {
                        NativeMethods.Power.SetThreadInformation(
                            thread, ThreadInformationClass.ThreadPowerThrottling,
                            ref eco, NativeMethods.Power.ThreadStateSize());
                    }
                    catch { }

                    var mem = new ThreadMemoryPriorityInfo { MemoryPriority = snap.MemoryPriority };
                    try
                    {
                        NativeMethods.Power.SetThreadInformation(
                            thread, ThreadInformationClass.ThreadMemoryPriority,
                            ref mem, memSize);
                    }
                    catch { }

                    try
                    {
                        var ideal = new ProcessorNumber
                        {
                            Group = snap.IdealGroup,
                            Number = snap.IdealNumber,
                            Reserved = 0,
                        };
                        NativeMethods.Affinity.SetThreadIdealProcessorEx(thread, ref ideal, IntPtr.Zero);
                    }
                    catch { }

                    try
                    {
                        if (snap.CpuSets is { Length: > 0 })
                            NativeMethods.CpuSets.SetThreadSelectedCpuSets(thread, snap.CpuSets, (uint)snap.CpuSets.Length);
                        else
                            NativeMethods.CpuSets.SetThreadSelectedCpuSets(thread, null!, 0);
                    }
                    catch { }
                }
                catch { }
            }
        }
        catch { }
    }

    // ─── helpers ────────────────────────────────────────────────────

    private static uint[]? ReadProcessCpuSets(SafeHandle process)
    {
        // SafeHandle downcast: callers pass SafeProcessHandle.
        if (process is not SafeProcessHandle h || h.IsInvalid) return null;
        if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, null, 0, out uint required))
        {
            if (Marshal.GetLastWin32Error() != 122) return null; // 122 = need buffer
        }
        if (required == 0) return Array.Empty<uint>();
        uint[] ids = new uint[required];
        if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, ids, required, out _)) return null;
        return ids;
    }

    private static uint[]? ReadThreadCpuSets(SafeHandle thread)
    {
        if (thread is not SafeThreadHandle h || h.IsInvalid) return null;
        if (!NativeMethods.CpuSets.GetThreadSelectedCpuSets(h, null, 0, out uint required))
        {
            if (Marshal.GetLastWin32Error() != 122) return null;
        }
        if (required == 0) return Array.Empty<uint>(); // inheriting process defaults
        uint[] ids = new uint[required];
        if (!NativeMethods.CpuSets.GetThreadSelectedCpuSets(h, ids, required, out _)) return null;
        return ids;
    }

    /// <summary>Full system CPU Set ID list (sync): the "unrestricted" restore target.</summary>
    internal static uint[] ReadSystemCpuSetIds()
    {
        try
        {
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0))
            {
                if (Marshal.GetLastWin32Error() != 122) return Array.Empty<uint>();
            }
            if (needed == 0 || needed > 64 * 1024 * 1024) return Array.Empty<uint>();
            byte[] buffer = new byte[needed];
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(buffer, needed, out _, IntPtr.Zero, 0))
                return Array.Empty<uint>();

            var ids = new List<uint>();
            int offset = 0, guard = 0;
            while (offset + 20 <= buffer.Length && guard++ < 2048)
            {
                uint size = BitConverter.ToUInt32(buffer, offset);
                byte type = buffer[offset + 4];
                if (size < 20 || offset + size > buffer.Length) break;
                if (type == 0) // CpuSetInformation
                    ids.Add(BitConverter.ToUInt32(buffer, offset + 8));
                offset += (int)size;
            }
            return ids.ToArray();
        }
        catch { return Array.Empty<uint>(); }
    }
}
