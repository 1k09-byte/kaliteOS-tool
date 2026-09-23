using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Native;

namespace kaliteConfig.Services;

/// <summary>
/// Thread-level tuning backed ONLY by documented Win32 APIs:
/// processthreadsapi (OpenThread, Get/SetThreadPriority, Get/SetThreadPriorityBoost,
/// Get/SetThreadGroupAffinity, SetThreadIdealProcessorEx, SuspendThread, ResumeThread,
/// TerminateThread) and SetThreadInformation with ThreadPowerThrottling /
/// ThreadMemoryPriority (MEMORY_PRIORITY_INFORMATION, levels 1-5).
/// Deliberately no NT_STATUS info-class tricks: thread I/O priority has no
/// documented Win32 setter, so it is not offered.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class ThreadTuningService
{
    public async Task SetPriorityAsync(uint tid, int priority)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.SetThreadPriority(thread, priority))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread priority failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> GetBoostAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool Svetlana))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread priority boost failed."));
            }

            return !Svetlana;
        }).ConfigureAwait(false);
    }

    public async Task SetBoostAsync(uint tid, bool enabled)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SetInformation, false, tid);
                
                if (!thread.IsInvalid)
                {
                    NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: !enabled);
                }
            }
            catch { }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-thread Efficiency Mode (EcoQoS) via SetThreadInformation. Uses the
    /// THREAD_POWER_THROTTLING_EXECUTION_SPEED mask (0x1) - note this differs
    /// from the process-level mask (0x4) on purpose.
    /// </summary>
    public async Task SetEfficiencyAsync(uint tid, bool enabled)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var state = new ThreadPowerThrottlingState
            {
                Version = NativeMethods.Power.ThreadVersion,
                ControlMask = NativeMethods.Power.ThreadExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ThreadExecutionSpeed : 0,
            };
            if (!NativeMethods.Power.SetThreadInformation(
                thread, ThreadInformationClass.ThreadPowerThrottling,
                ref state, NativeMethods.Power.ThreadStateSize()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread Efficiency Mode failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> GetEfficiencyAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            // NOTE: despite the docs, GetThreadInformation demands full
            // THREAD_QUERY_INFORMATION on this build - LIMITED fails with
            // access denied even on our own threads (verified live).
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var state = new ThreadPowerThrottlingState
            {
                Version = NativeMethods.Power.ThreadVersion,
            };
            if (!NativeMethods.Power.GetThreadInformation(
                thread, ThreadInformationClass.ThreadPowerThrottling,
                ref state, NativeMethods.Power.ThreadStateSize()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread Efficiency Mode failed."));
            }

            return (state.StateMask & NativeMethods.Power.ThreadExecutionSpeed) != 0;
        }).ConfigureAwait(false);
    }

    public async Task<(ushort Group, ulong Mask)> GetAffinityStateAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var affinity))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread affinity failed."));
            }
            return (affinity.Group, affinity.Mask);
        }).ConfigureAwait(false);
    }

    public async Task<string> GetAffinityDescriptionAsync(uint tid)
    {
        var state = await GetAffinityStateAsync(tid).ConfigureAwait(false);
        var cpus = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((state.Mask & (1UL << i)) != 0) cpus.Add(i.ToString());
        }
        return $"Group {state.Group}: CPU {string.Join(", ", cpus)} (0x{state.Mask:X})";
    }

    public async Task<ulong> GetAffinityAsync(uint tid) =>
        (await GetAffinityStateAsync(tid).ConfigureAwait(false)).Mask;

    public Task SetAffinityAsync(uint tid, ulong mask) =>
        SetAffinityAsync(tid, null, mask);

    public async Task SetAffinityAsync(uint tid, ushort? group, ulong mask)
    {
        if (mask == 0) throw new ArgumentException("The affinity mask must contain at least one processor.", nameof(mask));

        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);

            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var current))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread affinity failed."));
            }

            // A GROUP_AFFINITY mask is local to its processor group. Never
            // treat CPU 0 in group 1 as CPU 64 in group 0. Preserve the live
            // group unless the caller explicitly selected another group.
            current.Group = group ?? current.Group;
            ushort active = NativeMethods.Affinity.GetActiveProcessorCount(current.Group);
            ulong validBits = active >= 64 ? ulong.MaxValue : ((1UL << active) - 1UL);
            if ((mask & ~validBits) != 0)
            {
                throw new InvalidOperationException($"CPU mask 0x{mask:X} contains processors unavailable in group {current.Group} (active CPUs: {active}).");
            }

            current.Mask = mask;
            if (!NativeMethods.Affinity.SetThreadGroupAffinity(thread, ref current, IntPtr.Zero))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread affinity failed."));
            }

            // Verify the kernel accepted the change before reporting success.
            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var applied)
                || applied.Group != current.Group
                || applied.Mask != mask)
            {
                throw new InvalidOperationException("Windows did not apply the requested thread affinity mask.");
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Reads the live ideal processor (documented GetThreadIdealProcessorEx).</summary>
    public async Task<(ushort Group, byte Number)> GetIdealProcessorAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            // BOOL return, processor number in the out parameter. The old
            // "== (DWORD)-1" check could never be true, so a failed read used to
            // report a bogus ideal processor instead of failing.
            if (!NativeMethods.Affinity.GetThreadIdealProcessorEx(thread, out var current))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread ideal processor failed."));
            }
            return (current.Group, current.Number);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the thread's ideal processor and verifies it by reading the value
    /// back, exactly like <see cref="SetAffinityAsync"/>.
    ///
    /// The result MUST be checked: Windows refuses a processor the thread's
    /// affinity mask excludes (ERROR_GEN_FAILURE 31) or one that does not exist
    /// (ERROR_INVALID_PARAMETER 87). This used to ignore the BOOL entirely, so
    /// the dialog said "Ideal processor set to CPU 10" while nothing changed -
    /// the reported "not applying properly" bug.
    /// </summary>
    public async Task SetIdealProcessorAsync(uint tid, ushort group, byte index)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);

            var requested = new ProcessorNumber
            {
                Group = group,
                Number = index,
                Reserved = 0,
            };

            if (!NativeMethods.Affinity.SetThreadIdealProcessorEx(thread, ref requested, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    DescribeIdealProcessorFailure(tid, group, index, Marshal.GetLastWin32Error(), thread));
            }

            if (NativeMethods.Affinity.GetThreadIdealProcessorEx(thread, out var applied)
                && (applied.Group != group || applied.Number != index))
            {
                throw new InvalidOperationException(
                    $"Windows kept group {applied.Group} CPU {applied.Number} as the ideal processor instead of CPU {index}.");
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a rejected ideal-processor write into something the user can act
    /// on. Error 31 has two very different causes and they need different
    /// advice:
    ///   - the CPU is outside the thread's affinity mask (the affinity grid or a
    ///     saved rule pinned the thread to a few cores);
    ///   - the CPU is inside the mask but belongs to the CPU-set partition's
    ///     reservation, which only threads inside that partition may use - on a
    ///     16-thread box with 0xFC00 reserved, every CPU from 10 up is refused.
    /// </summary>
    private static string DescribeIdealProcessorFailure(
        uint tid, ushort group, byte index, int error, SafeThreadHandle thread)
    {
        ulong affinity = TryReadAffinityMask(thread);
        bool insideMask = index < 64 && affinity != 0 && (affinity & (1UL << index)) != 0;
        string usable = DescribeUsableCpus(affinity, TryReadReservedCpuMask());
        string usableHint = usable.Length == 0 ? "" : $" Usable CPUs: {usable}.";

        return error switch
        {
            31 when affinity != 0 && !insideMask =>
                $"CPU {index} is outside this thread's affinity mask, so Windows refused it." + usableHint +
                " Tick one of those CPUs, or widen affinity first.",

            31 => $"CPU {index} is not available to this thread (Windows error 31)." + usableHint +
                  " CPUs held by the system's reserved CPU-set partition can only be used by " +
                  "threads inside that partition.",

            87 => $"CPU {index} does not exist in processor group {group}." + usableHint,

            5 => $"Windows denied access to thread {tid} (protected thread or insufficient rights).",

            _ => $"Setting the ideal processor to CPU {index} failed (error {error})." + usableHint,
        };
    }

    private static ulong TryReadAffinityMask(SafeThreadHandle thread)
    {
        try
        {
            return NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var affinity) ? affinity.Mask : 0;
        }
        catch { return 0; }
    }

    private static ulong? TryReadReservedCpuMask()
    {
        try { return new ReservedCpuSetsService().GetReservedCpuMask(); }
        catch { return null; }
    }

    /// <summary>
    /// "0/1/2/3" or "0-9 (0x03FF)" for the CPUs a thread can really take: its
    /// affinity mask minus the CPU-set partition's reservation. Reserved CPUs
    /// are only usable by threads inside that partition, which is exactly why
    /// Windows refuses them for everything else.
    /// </summary>
    private static string DescribeUsableCpus(ulong affinity, ulong? reserved)
    {
        if (affinity == 0) return string.Empty;
        ulong usable = reserved is { } reservedMask ? affinity & ~reservedMask : affinity;
        var cpus = new List<int>();
        for (int i = 0; i < 64; i++)
        {
            if ((usable & (1UL << i)) != 0) cpus.Add(i);
        }
        if (cpus.Count == 0) return string.Empty;
        return cpus.Count <= 8 ? string.Join('/', cpus) : $"0-{cpus[^1]} (0x{usable:X})";
    }

    public async Task SuspendThreadAsync(uint tid)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SuspendResume, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (NativeMethods.Threads.SuspendThread(thread) == uint.MaxValue)
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Suspending thread failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task ResumeThreadAsync(uint tid)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SuspendResume, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (NativeMethods.Threads.ResumeThread(thread) == uint.MaxValue)
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Resuming thread failed."));
            }
        }).ConfigureAwait(false);
    }

    public async Task TerminateThreadAsync(uint tid, uint exitCode = 1)
    {
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.Terminate, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Threads.TerminateThread(thread, exitCode))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Terminating thread failed."));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Thread memory priority 1 (very low) … 5 (normal), per
    /// MEMORY_PRIORITY_INFORMATION (documented Win32 API).</summary>
    public async Task<uint> GetMemoryPriorityAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var info = new ThreadMemoryPriorityInfo();
            if (!NativeMethods.Power.GetThreadInformation(
                thread, ThreadInformationClass.ThreadMemoryPriority,
                ref info, (uint)Marshal.SizeOf<ThreadMemoryPriorityInfo>()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread memory priority failed."));
            }
            return info.MemoryPriority;
        }).ConfigureAwait(false);
    }

    public async Task SetMemoryPriorityAsync(uint tid, uint level)
    {
        level = Math.Clamp(level, 1, 5);
        await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            var info = new ThreadMemoryPriorityInfo { MemoryPriority = level };
            if (!NativeMethods.Power.SetThreadInformation(
                thread, ThreadInformationClass.ThreadMemoryPriority,
                ref info, (uint)Marshal.SizeOf<ThreadMemoryPriorityInfo>()))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread memory priority failed."));
            }
        }).ConfigureAwait(false);
    }
}

