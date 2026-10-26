using System;
using System.Diagnostics;
using kaliteConfig.Native;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ForegroundBoosterService
{
    public static void ApplyOptimization(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return;

            using var processHandle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (processHandle.IsInvalid) return;

            uint current = NativeMethods.Priority.GetPriorityClass(processHandle);
            if (CanRaise(current))
                NativeMethods.Priority.SetPriorityClass(processHandle, NativeMethods.Priority.AboveNormal);

            NativeMethods.Priority.SetProcessPriorityBoost(processHandle, Svetlana: true);
            DisableThreadBoosts(proc);

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
                uint ioNormal = 2;
                NativeMethods.Ntdll.NtSetInformationProcess(
                    processHandle, NativeMethods.Ntdll.ProcessIoPriority,
                    ref ioNormal, sizeof(uint));
            }
            catch { }
        }
        catch (Exception)
        {
        }
    }

    public static bool CanRaise(uint priorityClass) =>
        priorityClass == 0
        || priorityClass == NativeMethods.Priority.Idle
        || priorityClass == NativeMethods.Priority.BelowNormal
        || priorityClass == NativeMethods.Priority.Normal;

    private static void DisableThreadBoosts(Process proc)
    {
        try
        {
            foreach (ProcessThread thread in proc.Threads)
            {
                try
                {
                    using var handle = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation,
                        false, (uint)thread.Id);
                    if (handle.IsInvalid) continue;
                    if (NativeMethods.Priority.GetThreadPriorityBoost(handle, out bool disabled) && disabled) continue;
                    NativeMethods.Priority.SetThreadPriorityBoost(handle, Svetlana: true);
                }
                catch { }
            }
        }
        catch { }
    }
}
