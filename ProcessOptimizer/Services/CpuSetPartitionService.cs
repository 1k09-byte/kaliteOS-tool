using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Native;

namespace kaliteConfig.ProcessOptimizer.Services;

/// <summary>
/// Exclusive CPU Sets partition: game gets its own P-core CCX sets,
/// background stays off them. Harder than affinity — the scheduler will
/// not place default-set threads outside their sets. Core 0 (DPC/ISR)
/// is never given to the game. Restore map handles session end + PID reuse.
/// </summary>
public static class CpuSetPartitionService
{
    private static readonly ConcurrentDictionary<int, (long StartTicks, uint[] Original)> _restore = new();

    public static uint[]? LastGameSets { get; private set; }
    public static uint[]? LastBackgroundSets { get; private set; }
    public static uint[]? LastReservedSets { get; private set; }

    /// <summary>
    /// Kernel-reserved core mask (ReservedCpuSets in Session Manager\kernel).
    /// Sets whose logical processor bit falls inside this mask are excluded
    /// from BOTH partitions — the kernel never schedules user threads there,
    /// so assigning them would silently shrink a partition to nothing.
    /// </summary>
    private static HashSet<uint> GetReservedSetIds(List<CpuSetEntry> topo)
    {
        var reserved = new HashSet<uint>();
        try
        {
            ulong? mask = new kaliteConfig.Services.ReservedCpuSetsService().GetReservedCpuMask();
            if (mask is null or 0) return reserved;
            foreach (var e in topo)
            {
                ulong bit = e.LogicalIndex < 64 ? 1UL << e.LogicalIndex : 0;
                if (bit != 0 && (mask.Value & bit) != 0) reserved.Add(e.Id);
            }
        }
        catch { }
        return reserved;
    }

    public static bool ApplyExclusivePartition(int gamePid)
    {
        try
        {
            List<CpuSetEntry> topo = QueryTopology();
            if (topo.Count == 0) return false;

            var reserved = GetReservedSetIds(topo);

            // P-class = lowest EfficiencyClass value (matches TopologyService: 0 = Performance).
            byte pClass = topo.Min(e => e.EfficiencyClass);
            var pSets = topo.Where(e => e.EfficiencyClass == pClass && !reserved.Contains(e.Id)).ToList();

            // Reserve Core 0's logical sets for OS/DPC.
            var usable = pSets.Where(e => e.CoreIndex != 0).ToList();
            if (usable.Count == 0) usable = pSets;
            if (usable.Count == 0) return false;

            // Simple, correct model: the GAME gets ALL usable P-core sets
            // (whole CCXs, whole SMT siblings — the engine spreads itself),
            // and background work gets ALL E-core sets + any P-cores left
            // out of the game (e.g. core 0 when usable P-cores exceed the
            // game's needs). On non-hybrid CPUs the background partition is
            // simply everything P the game didn't take.
            uint[] gameSets = usable.Select(e => e.Id).ToArray();
            if (gameSets.Length == 0) return false;

            var gameSet = new HashSet<uint>(gameSets);
            // Background partition: E-cores first, then non-game P-cores,
            // everything kernel-reserved excluded.
            uint[] bgSets = topo
                .Where(e => !gameSet.Contains(e.Id) && !reserved.Contains(e.Id))
                .OrderBy(e => e.EfficiencyClass == pClass ? 1 : 0) // E-cores listed first (cosmetic)
                .Select(e => e.Id).ToArray();
            if (bgSets.Length == 0) return false;

            // Remember original game default sets for restore (PID-reuse safe).
            long ticks = 0;
            try { ticks = Process.GetProcessById(gamePid).StartTime.Ticks; } catch { return false; }
            uint[]? orig = GetProcessSets(gamePid);
            _restore[gamePid] = (ticks, orig ?? Array.Empty<uint>());

            if (!SetProcessSets(gamePid, gameSets)) return false;

            // Pin game threads into the partition too (inherit would suffice,
            // but explicit selected-sets stop already-running threads migrating).
            try
            {
                using var proc = Process.GetProcessById(gamePid);
                foreach (ProcessThread? t in proc.Threads)
                {
                    if (t == null) continue;
                    try
                    {
                        using var hThread = NativeMethods.Handles.OpenThread(
                            NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                            false, (uint)t.Id);
                        if (!hThread.IsInvalid)
                            NativeMethods.CpuSets.SetThreadSelectedCpuSets(hThread, gameSets, (uint)gameSets.Length);
                    }
                    catch { }
                }
            }
            catch { }

            LastGameSets = gameSets;
            LastBackgroundSets = bgSets;
            LastReservedSets = reserved.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CpuSetPartition] {ex.Message}");
            return false;
        }
    }

    /// <summary>Background sets for throttled processes (stays off game CCX).</summary>
    public static uint[]? GetBackgroundSets() => LastBackgroundSets;

    /// <summary>Topology probe for services that need set↔logical-index mapping.</summary>
    public static List<CpuSetEntry> QueryTopologyPublic() => QueryTopology();

    public static void RestorePartition(int gamePid)
    {
        try
        {
            if (!_restore.TryRemove(gamePid, out var saved)) return;
            try
            {
                using var p = Process.GetProcessById(gamePid);
                if (p.HasExited || p.StartTime.Ticks != saved.StartTicks) return;
            }
            catch { return; }
            if (saved.Original.Length > 0)
                SetProcessSets(gamePid, saved.Original);
            // Empty original = was unrestricted; clear by setting all sets.
            else
            {
                var all = QueryTopology().Select(e => e.Id).ToArray();
                if (all.Length > 0) SetProcessSets(gamePid, all);
            }
        }
        catch { }
    }

    public static void RestoreAll()
    {
        foreach (int pid in _restore.Keys.ToArray())
            RestorePartition(pid);
        LastGameSets = null;
        LastBackgroundSets = null;
    }

    // ── raw helpers (sync, no Task wrapper — called from booster path) ──

    private static List<CpuSetEntry> QueryTopology()
    {
        var list = new List<CpuSetEntry>();
        try
        {
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0))
            {
                if (Marshal.GetLastWin32Error() != 122) return list;
            }
            if (needed == 0 || needed > 64 * 1024 * 1024) return list;
            byte[] buffer = new byte[needed];
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(buffer, needed, out _, IntPtr.Zero, 0))
                return list;
            int offset = 0, guard = 0;
            while (offset + 20 <= buffer.Length && guard++ < 2048)
            {
                uint size = BitConverter.ToUInt32(buffer, offset);
                byte type = buffer[offset + 4];
                if (size < 20 || offset + size > buffer.Length) break;
                if (type == 0)
                {
                    byte flags = buffer[offset + 19];
                    list.Add(new CpuSetEntry
                    {
                        Id = BitConverter.ToUInt32(buffer, offset + 8),
                        Group = BitConverter.ToUInt16(buffer, offset + 12),
                        LogicalIndex = buffer[offset + 14],
                        CoreIndex = buffer[offset + 15],
                        LastLevelCacheIndex = buffer[offset + 16],
                        NumaNodeIndex = buffer[offset + 17],
                        EfficiencyClass = buffer[offset + 18],
                        Parked = (flags & 0x01) != 0,
                        Allocated = (flags & 0x02) != 0,
                    });
                }
                offset += (int)size;
            }
        }
        catch { }
        return list;
    }

    private static uint[]? GetProcessSets(int pid)
    {
        try
        {
            using var h = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (h.IsInvalid) return null;
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, null, 0, out uint required))
            {
                if (Marshal.GetLastWin32Error() != 122) return null;
            }
            if (required == 0) return Array.Empty<uint>();
            uint[] ids = new uint[required];
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, ids, required, out _)) return null;
            return ids;
        }
        catch { return null; }
    }

    private static bool SetProcessSets(int pid, uint[] ids)
    {
        try
        {
            using var h = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (h.IsInvalid) return false;
            return NativeMethods.CpuSets.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
        }
        catch { return false; }
    }
}
