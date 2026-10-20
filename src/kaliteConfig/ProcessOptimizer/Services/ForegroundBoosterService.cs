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

            NativeMethods.Priority.SetPriorityClass(processHandle, NativeMethods.Priority.AboveNormal);

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
}
