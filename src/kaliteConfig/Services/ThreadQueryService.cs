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
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Native;

namespace kaliteConfig.Services;

/// <summary>One live thread row for the rule editor's thread picker.</summary>
public sealed class LiveThreadInfo
{
    public int Tid { get; set; }
    public int Pid { get; set; }
    public string Description { get; set; } = string.Empty;
    public string StartAddress { get; set; } = string.Empty;
    public int RelativeValue { get; set; }
    public string RelativeText { get; set; } = string.Empty;
    public int Base { get; set; }
}

/// <summary>One Threads-dialog row, with every column gathered in a single pass.</summary>
public sealed class LiveThreadDetail
{
    public int Tid { get; set; }
    public string Description { get; set; } = string.Empty;
    public string StartAddress { get; set; } = string.Empty;

    /// <summary>Current thread priority level (-15 … +15), or null when unreadable.</summary>
    public int? PriorityLevel { get; set; }

    /// <summary>Null = boost state could not be read (treat as allowed).</summary>
    public bool? BoostAllowed { get; set; }

    /// <summary>Affinity mask, null when unreadable.</summary>
    public ulong? AffinityMask { get; set; }

    /// <summary>Preferred CPU, null when the thread has no preference.</summary>
    public int? IdealCpu { get; set; }
    public int? IdealGroup { get; set; }

    /// <summary>Total processor time in seconds, 0 when unreadable.</summary>
    public double CpuSeconds { get; set; }
}

/// <summary>
/// Enumerates live threads using OS thread names only - no DbgHelp, so nothing
/// ever blocks on symbol downloads.
/// </summary>
public sealed class ThreadQueryService
{
    private static readonly ProtectedProcessService _protectedProcessService = new();

    /// <summary>System Informer style: "Time critical", "Below normal", "Custom (3)", "Custom (-4)".</summary>
    public static string FormatRelative(int level) => level switch
    {
        -15 => "Idle",
        -2 => "Lowest",
        -1 => "Below normal",
        0 => "Normal",
        1 => "Above normal",
        2 => "Highest",
        15 => "Time critical",
        _ => $"Custom ({level})",
    };

    /// <summary>Threads with the fields the rule editor's thread picker needs.</summary>
    public static async Task<List<LiveThreadInfo>> ListThreadsAsync(int pid)
    {
        return await Task.Run(() =>
        {
            var rows = new List<LiveThreadInfo>();
            Process proc;
            try
            {
                proc = Process.GetProcessById(pid);
            }
            catch
            {
                return rows;
            }

            using (proc)
            {
                ProcessThreadCollection threads;
                try
                {
                    threads = proc.Threads;
                }
                catch
                {
                    return rows;
                }

                // Modules are re-materialised on every access, so reading them per thread
                // made this quadratic on a large process.
                ModuleRange[] modules = ReadModuleRanges(proc);

                foreach (ProcessThread t in threads)
                {
                    string desc = string.Empty;
                    string start = "-";
                    int relative = 0;
                    int basePri = 0;
                    try
                    {
                        desc = NativeSnapshotService.TryGetThreadDescription((uint)t.Id)
                            ?? "(unnamed)";
                        start = ResolveStart(t, modules);
                        relative = (int)t.PriorityLevel;
                        basePri = t.BasePriority;
                    }
                    catch
                    {
                        continue;
                    }

                    rows.Add(new LiveThreadInfo
                    {
                        Tid = t.Id,
                        Pid = pid,
                        Description = desc,
                        StartAddress = start,
                        RelativeValue = relative,
                        RelativeText = FormatRelative(relative),
                        Base = basePri
                    });
                }
            }

            return rows.OrderBy(r => r.Tid).ToList();
        }).ConfigureAwait(false);
    }

    /// <summary>Every Threads-dialog column for every thread, using one handle per thread.</summary>
    public static async Task<List<LiveThreadDetail>> ListThreadsDetailedAsync(int pid)
    {
        return await Task.Run(() =>
        {
            var rows = new List<LiveThreadDetail>();
            Process proc;
            try
            {
                proc = Process.GetProcessById(pid);
            }
            catch
            {
                return rows;
            }

            using (proc)
            {
                ProcessThreadCollection threads;
                try
                {
                    threads = proc.Threads;
                }
                catch
                {
                    return rows;
                }

                ModuleRange[] modules = ReadModuleRanges(proc);

                foreach (ProcessThread t in threads)
                {
                    if (t == null) continue;
                    int tid = t.Id;
                    var row = new LiveThreadDetail { Tid = tid, StartAddress = "-" };

                    // Free from the already-materialised ProcessThread, and readable even
                    // when the handle below is refused.
                    try { row.CpuSeconds = t.TotalProcessorTime.TotalSeconds; } catch { }

                    try
                    {
                        using var h = NativeMethods.Handles.OpenThread(
                            NativeMethods.ThreadAccess.QueryInformation |
                            NativeMethods.ThreadAccess.QueryLimitedInformation,
                            false, (uint)tid);
                        if (h.IsInvalid) throw new UnauthorizedAccessException();

                        row.Description = NativeSnapshotService.QueryThreadDescription(h) ?? "(unnamed)";
                        row.StartAddress = ResolveStart(t, modules, h);

                        row.PriorityLevel = NativeMethods.Priority.GetThreadPriority(h);
                        try
                        {
                            if (NativeMethods.Priority.GetThreadPriorityBoost(h, out bool boostDisabled))
                                row.BoostAllowed = !boostDisabled; // API flag is inverted
                        }
                        catch { }

                        try
                        {
                            if (NativeMethods.Affinity.GetThreadGroupAffinity(h, out var affinity))
                                row.AffinityMask = affinity.Mask;
                        }
                        catch { }

                        try
                        {
                            if (NativeMethods.Affinity.GetThreadIdealProcessorEx(h, out var ideal)
                                && !(ideal.Group == 0xFF && ideal.Number == 0xFF))
                            {
                                row.IdealCpu = ideal.Number;
                                row.IdealGroup = ideal.Group;
                            }
                        }
                        catch { }
                    }
                    catch
                    {
                        // Keep the cheap reads rather than dropping the row entirely.
                        if (string.IsNullOrEmpty(row.Description)) row.Description = "(unnamed)";
                    }

                    rows.Add(row);
                }
            }

            return rows;
        }).ConfigureAwait(false);
    }

    /// <summary>First running process matching a rule pattern (supports * wildcards, with/without .exe).</summary>
    public static int FindPid(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return 0;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (ProfileMatcher.Matches(pattern, proc.ProcessName))
                    {
                        return proc.Id;
                    }
                }
                catch
                {
                    continue;
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }
        return 0;
    }

    private readonly record struct ModuleRange(long Base, long Size, string Name);

    /// <summary>Modules as sorted address ranges; empty when the list is unreadable.</summary>
    private static ModuleRange[] ReadModuleRanges(Process proc)
    {
        try
        {
            var list = new List<ModuleRange>();
            foreach (ProcessModule mod in proc.Modules)
            {
                list.Add(new ModuleRange(
                    mod.BaseAddress.ToInt64(), mod.ModuleMemorySize, mod.ModuleName));
            }
            list.Sort(static (a, b) => a.Base.CompareTo(b.Base));
            return list.ToArray();
        }
        catch
        {
            return Array.Empty<ModuleRange>();
        }
    }

    /// <summary>"mod.dll+0x1A2B" for an address inside a module, else the raw address.</summary>
    private static string ResolveStart(ProcessThread t, ModuleRange[] modules, SafeThreadHandle? handle = null)
    {
        long addr = 0;
        try
        {
            addr = handle != null
                ? NativeSnapshotService.QueryWin32StartAddress(handle) ?? 0
                : NativeSnapshotService.TryGetWin32StartAddress((uint)t.Id) ?? 0;
        }
        catch
        {
        }

        if (addr == 0)
        {
            try
            {
                addr = t.StartAddress.ToInt64();
            }
            catch
            {
                return "-";
            }
        }

        return FormatAddress(addr, modules);
    }

    /// <summary>"mod.dll+0x1A2B" or "0x7FF..." - binary search over sorted ranges.</summary>
    private static string FormatAddress(long addr, ModuleRange[] modules)
    {
        int lo = 0, hi = modules.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ModuleRange m = modules[mid];
            if (addr < m.Base) hi = mid - 1;
            else if (addr >= m.Base + m.Size) lo = mid + 1;
            else return $"{m.Name}+0x{addr - m.Base:X}";
        }

        return $"0x{addr:X}";
    }
}