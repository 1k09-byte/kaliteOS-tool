using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using kaliteConfig.Services;
using kaliteConfig.Native;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ForegroundBoosterService
{
    /// <summary>
    /// Peak-oriented game boost:
    /// - Process High, boost ENABLED, Eco OFF, MemoryPriority 5, IO Normal+
    /// - P-cores only, best-CCX aware, SMT bypass (no E-cores, no migration)
    /// - Per-thread: Eco OFF, MemoryPriority 5, boost enabled, IdealProcessor
    ///   spread across game P-cores, hot threads lifted to Highest.
    /// </summary>
    public static void ApplyOptimization(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;

            using var processHandle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (!processHandle.IsInvalid)
            {
                NativeMethods.Priority.SetPriorityClass(processHandle, NativeMethods.Priority.High);
                // Boost ENABLED on game (Disable=false) so the OS extends quanta on wake.
                NativeMethods.Priority.SetProcessPriorityBoost(processHandle, false);

                var ecoOff = new ProcessPowerThrottlingState
                {
                    Version = NativeMethods.Power.Version,
                    ControlMask = NativeMethods.Power.ExecutionSpeed,
                    StateMask = 0,
                };
                NativeMethods.Power.SetProcessInformation(
                    processHandle, ProcessInformationClass.ProcessPowerThrottling,
                    ref ecoOff, NativeMethods.Power.StateSize());

                var memMax = new ProcessMemoryPriorityInfo { MemoryPriority = 5 };
                NativeMethods.Power.SetProcessInformation(
                    processHandle, ProcessInformationClass.ProcessMemoryPriority,
                    ref memMax, NativeMethods.Power.MemoryPrioritySize());

                try
                {
                    uint ioNormal = 2; // Normal; 3 would be High and risks audio/DWM inversion
                    NativeMethods.Ntdll.NtSetInformationProcess(
                        processHandle, NativeMethods.Ntdll.ProcessIoPriority,
                        ref ioNormal, sizeof(uint));
                }
                catch { }
            }

            // P-only, best-CCX physical mask (SMT bypass, no E-cores).
            ulong gameMask = ComputeGameMask();
            try
            {
                if (gameMask != 0)
                    proc.ProcessorAffinity = (IntPtr)(long)gameMask;
            }
            catch { }

            // Hard partition via CPU Sets (beats affinity: scheduler cannot
            // place default-set background threads inside the game CCX).
            CpuSetPartitionService.ApplyExclusivePartition(pid);

            BoostGameThreads(proc, gameMask);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ForegroundBoosterService] Failed to boost PID {pid}: {ex.Message}");
        }
    }

    private static ulong ComputeGameMask()
    {
        try
        {
            var topo = TopologyService.Get();
            if (topo.PerformanceCoreMasks.Count == 0) return 0;

            // Primary thread bit of each P-core.
            var primaries = topo.PerformanceCoreMasks
                .Where(m => m != 0)
                .Select(m => m & ~(m - 1))
                .Where(b => b != 0)
                .ToList();
            if (primaries.Count == 0) return 0;

            // Best CCX = L3 domain holding the most P-core primaries.
            if (topo.CoreComplexMasks.Count > 0)
            {
                ulong best = 0;
                int bestCount = 0;
                foreach (ulong ccx in topo.CoreComplexMasks)
                {
                    if (ccx == 0) continue;
                    int count = primaries.Count(p => (p & ccx) != 0);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = ccx;
                    }
                }
                if (bestCount > 0)
                {
                    ulong inCcX = 0;
                    foreach (ulong p in primaries)
                        if ((p & best) != 0) inCcX |= p;
                    if (inCcX != 0) return inCcX;
                }
            }

            ulong all = 0;
            foreach (ulong p in primaries) all |= p;
            return all;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Boosts ONLY the hottest game threads (1-3 by CPU-time delta). Game
    /// engines set their own thread priorities on purpose (audio, streaming,
    /// worker pools) — flattening every thread to Highest makes the render
    /// thread compete with its own helpers. The rest are left exactly as the
    /// engine set them. Hot threads get: Highest priority, ideal processor
    /// pinned one-per-physical-core (SMT sibling left empty), Eco off.
    /// </summary>
    internal static void BoostGameThreads(Process proc, ulong gameMask)
    {
        // Sorted P-core logical indices for IdealProcessor spreading.
        List<int> pCores = MaskToIndices(gameMask);
        if (pCores.Count == 0)
        {
            int n = Math.Max(1, Environment.ProcessorCount);
            pCores = Enumerable.Range(1, n - 1).ToList(); // skip Core 0
            if (pCores.Count == 0) pCores.Add(0);
        }

        var hot = FindHotThreads(proc, topN: 3);
        if (hot.Count == 0) return;

        int i = 0;
        foreach (uint tid in hot)
        {
            try
            {
                using var hThread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryLimitedInformation, false, tid);
                if (hThread.IsInvalid) continue;

                // Eco OFF per-thread.
                var ecoOff = new ThreadPowerThrottlingState
                {
                    Version = NativeMethods.Power.ThreadVersion,
                    ControlMask = NativeMethods.Power.ThreadExecutionSpeed,
                    StateMask = 0,
                };
                NativeMethods.Power.SetThreadInformation(
                    hThread, ThreadInformationClass.ThreadPowerThrottling,
                    ref ecoOff, NativeMethods.Power.ThreadStateSize());

                // Lift base thread priority to Highest (2). TimeCritical (15)
                // is deliberately avoided: with High process class it risks
                // DWM/audio inversion and hurts 1% lows.
                NativeMethods.Priority.SetThreadPriority(hThread, NativeMethods.ThreadPriorityLevel.Highest);

                // Pin ideal processor, one hot thread per PHYSICAL core:
                // step over SMT siblings so two hot threads never share a
                // core's execution ports. Soft hint, no hard affinity.
                if (i < pCores.Count)
                {
                    try
                    {
                        var ideal = new ProcessorNumber
                        {
                            Group = 0,
                            Number = (byte)pCores[i],
                            Reserved = 0
                        };
                        NativeMethods.Affinity.SetThreadIdealProcessorEx(hThread, ref ideal, IntPtr.Zero);
                    }
                    catch { }
                }
                i++;
            }
            catch { }
        }
    }

    /// <summary>
    /// Ranks the process's live threads by CPU-time delta over a short sample
    /// window and returns the top-N thread IDs. Two snapshots ~250 ms apart;
    /// threads that exited mid-sample are skipped.
    /// </summary>
    internal static List<uint> FindHotThreads(Process proc, int topN)
    {
        try
        {
            var first = new Dictionary<uint, TimeSpan>();
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                try { first[(uint)t.Id] = t.TotalProcessorTime; } catch { }
            }
            Thread.Sleep(250);
            proc.Refresh();
            var delta = new List<(uint Tid, TimeSpan Cpu)>(first.Count);
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                uint tid = (uint)t.Id;
                if (!first.TryGetValue(tid, out var t1)) continue;
                try
                {
                    var d = t.TotalProcessorTime - t1;
                    if (d > TimeSpan.Zero) delta.Add((tid, d));
                }
                catch { }
            }
            return delta.OrderByDescending(x => x.Cpu).Take(topN).Select(x => x.Tid).ToList();
        }
        catch { return new List<uint>(); }
    }

    private static List<int> MaskToIndices(ulong mask)
    {
        var list = new List<int>();
        for (int i = 0; i < 64; i++)
            if ((mask & (1UL << i)) != 0) list.Add(i);
        return list;
    }
}
