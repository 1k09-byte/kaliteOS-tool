using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Native;

namespace kaliteConfig.Services;

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
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: !enabled))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Setting thread priority boost failed."));
            }
            if (NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool Svetlana)
                && Svetlana == enabled)
            {
                throw CpuSetService.Friendly(tid, new InvalidOperationException("Thread priority boost change was not applied by the system."));
            }
        }).ConfigureAwait(false);
    }

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

            if (!NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var applied)
                || applied.Group != current.Group
                || applied.Mask != mask)
            {
                throw new InvalidOperationException("Windows did not apply the requested thread affinity mask.");
            }
        }).ConfigureAwait(false);
    }

    public async Task<(ushort Group, byte Number)> GetIdealProcessorAsync(uint tid)
    {
        return await CpuSetService.RunNativeAsync(() =>
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, tid);
            CpuSetService.ThrowIfInvalid(thread, tid);
            if (!NativeMethods.Affinity.GetThreadIdealProcessorEx(thread, out var current))
            {
                throw CpuSetService.Friendly(tid, NativeSnapshotService.LastError("Reading thread ideal processor failed."));
            }
            return (current.Group, current.Number);
        }).ConfigureAwait(false);
    }

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
