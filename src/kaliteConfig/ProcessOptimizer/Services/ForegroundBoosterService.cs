using System;
using System.Diagnostics;
using kaliteConfig.Native;

namespace kaliteConfig.ProcessOptimizer.Services;

/// <summary>
/// Raises the game process's scheduling and resource worthiness, and nothing else.
///
/// Deliberately ABSENT, each removed because it measurably cost more than it
/// bought (see Docs/GameMode.md):
/// - No affinity clamp. Clipping the process to one thread per physical core
///   capped a multithreaded engine at half the machine's threads and, on a
///   non-hybrid CPU, intersected with the CPU-set partition to leave the game
///   roughly three cores.
/// - No CPU-set partition. On a homogeneous CPU the partition gave the game
///   every core but one and pinned the ENTIRE rest of the system to core 0 -
///   the DPC/interrupt core.
/// - No thread rewriting. Raising individual threads to Highest and pinning
///   their ideal processors fights the engine's own scheduling (it sets thread
///   priorities on purpose: audio, streaming, worker pools). It is a Stage 2
///   A/B candidate, not a default.
/// - The priority-boost flag is left exactly as the user set it, so a process
///   marked "boost disabled" stays that way.
///
/// Priority lands on AboveNormal, not High: High starves the DWM/audio/driver
/// threads the game itself waits on, which shows up in the 1% lows.
/// </summary>
public static class ForegroundBoosterService
{
    /// <summary>
    /// Applies the peak-game settings to <paramref name="pid"/>: AboveNormal
    /// priority class, Efficiency mode OFF, memory priority 5, I/O priority
    /// Normal. Every one of these is recorded first by
    /// <see cref="ProcessStateSnapshotService.CaptureSnapshot"/> so
    /// <c>OptimizationSessionOrchestrator.EndSession</c> puts them back.
    /// </summary>
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
                uint ioNormal = 2; // Normal; 3 would be High and risks audio/DWM inversion
                NativeMethods.Ntdll.NtSetInformationProcess(
                    processHandle, NativeMethods.Ntdll.ProcessIoPriority,
                    ref ioNormal, sizeof(uint));
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ForegroundBoosterService] Failed to boost PID {pid}: {ex.Message}");
        }
    }
}
