using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Services;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.Services;

public enum CpuBoundState
{
    /// <summary>Not enough samples yet to decide.</summary>
    Sampling,
    /// <summary>Sustained high CPU: the process looks CPU-bound.</summary>
    Detected,
    /// <summary>CPU usage is below the detection threshold.</summary>
    NotDetected,
}

/// <summary>
/// Per-window CPU-bound detection state machine. Deliberately NOT part of
/// <see cref="GamingModeService"/>, which is shared app-wide: each Threads
/// window samples its own target process, so each owns its own detector.
/// Three consecutive samples at >= 50% of one core count as "CPU-bound" —
/// that is the regime where raising priority actually changes scheduling.
/// </summary>
public sealed class CpuBoundDetector
{
    // Detection: 3 samples x ~2 s tick = ~6 s of sustained load.
    private const int HotSampleThreshold = 3;
    // Percent of a SINGLE core. A single-threaded game pegging one core reads
    // ~100 here even on a 32-thread machine.
    private const double HotPercentThreshold = 50.0;

    private int _hotSamples;
    private bool _cpuBound;

    public bool IsCpuBound => _cpuBound;

    /// <summary>
    /// Feeds one whole-process CPU sample (percent of one core). The first
    /// call only seeds and returns Sampling.
    /// </summary>
    public CpuBoundState Observe(double cpuPercentOfOneCore)
    {
        if (cpuPercentOfOneCore < 0)
        {
            // Invalid sample (process died, access denied): don't reset the streak.
            return _cpuBound ? CpuBoundState.Detected : CpuBoundState.Sampling;
        }

        if (cpuPercentOfOneCore >= HotPercentThreshold)
        {
            _hotSamples++;
        }
        else
        {
            _hotSamples = 0;
        }

        _cpuBound = _hotSamples >= HotSampleThreshold;

        return _cpuBound ? CpuBoundState.Detected
             : _hotSamples > 0 ? CpuBoundState.Sampling
             : CpuBoundState.NotDetected;
    }

    /// <summary>Resets the detection streak (e.g. on manual restore).</summary>
    public void Reset()
    {
        _hotSamples = 0;
        _cpuBound = false;
    }
}

/// <summary>
/// One-shot result summary for the Gaming mode toggle, formatted for the status line.
/// </summary>
public sealed class GamingModeResult
{
    public bool Success { get; init; }
    public int LoweredCount { get; init; }
    public int EcoCount { get; init; }
    public int FailedCount { get; init; }
    public string TargetBefore { get; init; } = "?";
    public string TargetAfter { get; init; } = "?";
    public string Error { get; init; } = string.Empty;

    public string Summary =>
        Success
            ? $"target {TargetBefore} → {TargetAfter} · {LoweredCount} background lowered · {EcoCount} in Efficiency mode" +
              (FailedCount > 0 ? $" · {FailedCount} skipped (no access)" : "")
            : $"failed: {Error}";
}

/// <summary>
/// Gaming mode for the Threads window: raises the target process to Above
/// Normal (Efficiency mode forced OFF for the target), lowers every other
/// mutable background process to Below Normal AND turns their Efficiency
/// mode (EcoQoS) ON, and restores all original priority classes and eco
/// states when switched off (also on window close, so the system is never
/// left boosted).
///
/// CPU-bound detection is a pure counter state machine fed by the window's
/// 2 s tick: three consecutive samples at >= 50% of one core count as
/// "CPU-bound" — that is the regime where raising priority actually changes
/// scheduling. Detection never lowers anything else; that is opt-in via the
/// Gaming mode switch.
///
/// All changes funnel through one restore map so that "restore" always means
/// "the state before kaliteConfig touched anything", no matter how many
/// times auto-raise and gaming mode overlapped.
/// </summary>
public sealed class GamingModeService
{
    /// <summary>
    /// PID → (original priority class, original Efficiency mode). Eco is null
    /// when the original state could not be read — such processes get their
    /// priority lowered but are never eco-toggled. Restoration targets these.
    /// </summary>
    private readonly Dictionary<string, (uint Priority, bool? Eco)> _restoreMap = new();

    public bool IsActive { get; private set; }

    /// <summary>Number of processes we have changed and can restore.</summary>
    public int RestorableCount => _restoreMap.Count;

    /// <summary>
    /// Safely computes a unique composite key for a process to prevent PID reuse collisions.
    /// </summary>
    private static string GetProcessKey(int pid)
    {
        long ticks = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            ticks = p.StartTime.Ticks;
        }
        catch { }
        return $"{pid}_{ticks}";
    }

    private static string GetProcessKey(Process p)
    {
        long ticks = 0;
        try { ticks = p.StartTime.Ticks; } catch { }
        return $"{p.Id}_{ticks}";
    }

    /// <summary>
    /// Detection fired: raise the target Normal → Above Normal. Returns true
    /// when a raise was applied. Processes already above Normal are left
    /// alone (never pushed towards High/Realtime automatically) and never
    /// raised out of Below Normal/Idle — a throttled process was set there
    /// on purpose, either by the user or by the OS.
    /// </summary>
    public async Task<bool> AutoRaiseTargetAsync(int pid)
    {
        return await Task.Run(() =>
        {
            uint current = ReadPriorityClass(pid);
            if (current != NativeMethods.Priority.Normal)
            {
                return false; // already elevated, below normal, or unreadable
            }

            string key = GetProcessKey(pid);
            // Record the pre-change value so a later restore puts things back.
            if (!_restoreMap.ContainsKey(key))
            {
                _restoreMap[key] = (current, null);
            }

            return TrySetPriorityClass(pid, NativeMethods.Priority.AboveNormal);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Activates Gaming mode: records every mutable process's priority class,
    /// raises the target to High (never Realtime), lowers other background processes to
    /// Below Normal. Critical system processes, this app, and processes we
    /// can't read (no restore possible) are never touched.
    /// </summary>
    public async Task<GamingModeResult> ActivateAsync(
        int targetPid,
        IReadOnlyCollection<int>? protectedPids = null)
    {
        protectedPids ??= new[] { targetPid };

        return await Task.Run(() =>
        {
            try
            {
                // Push game logic exactly as before for the target PID.
                uint targetOriginal = ReadPriorityClass(targetPid);
                string targetBefore = ProcessTuningService.PriorityName(targetOriginal);

                // NOTE: the target is NOT touched here. ForegroundBoosterService
                // raises it to High (Eco OFF, boost on, mem/IO maxed) inside
                // StartSession, AFTER the orchestrator snapshots its original
                // state. Mutating it here would poison that baseline, and
                // Deactivate could then only restore the boosted values —
                // leaving the game stuck at High after Game Mode turns off.
                bool ok = true;

                // One-shot background demotion: every ordinary Normal process
                // drops to BelowNormal, gets its priority boost DISABLED
                // (no scheduler micro-spikes from OS quantum extensions),
                // and gets EcoQoS ON. Strict by design — High/Realtime/
                // AboveNormal (deliberate), Idle/BelowNormal (already low),
                // critical, protected, self, and the game are never touched.
                // Everything recorded here (true originals, pid+startTime
                // keyed) is restored by Deactivate.
                int lowered = 0;
                int ecoCount = 0;
                int failed = 0;
                int selfPid = Process.GetCurrentProcess().Id;

                GamingExemptionService.EnsureStarterFile();
                foreach (Process proc in Process.GetProcesses())
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = proc.Id;
                        name = proc.ProcessName + ".exe";
                    }
                    catch
                    {
                        continue; // died mid-enumeration
                    }

                    if (pid <= 4 || pid == targetPid || pid == selfPid) continue;
                    if (protectedPids.Contains(pid)) continue;
                    if (ProcessTuningService.IsCritical(name, pid)) continue;
                    if (ProcessTuningService.IsSelf(pid)) continue;
                    if (GamingExemptionService.IsExempt(proc.ProcessName)) continue;

                    // Contention gate: idle processes don't compete with the
                    // game (High priority preempts them instantly). Touching
                    // hundreds of idle processes is pure downside — each write
                    // churns the scheduler and the restore map for zero gain.
                    // Only demote processes actually burning CPU right now.
                    double cpuPct = GetCpuPercentSnapshot(pid);
                    if (cpuPct < 0.5) continue; // <0.5% CPU = not a contender

                    uint original = ReadPriorityClass(pid);
                    if (original == 0)
                    {
                        failed++; // no access — can't restore later, don't touch
                        continue;
                    }
                    if (original != NativeMethods.Priority.Normal) continue; // respect all non-Normal

                    bool? ecoOriginal = ReadEcoState(pid);

                    string key = GetProcessKey(pid);
                    if (!_restoreMap.ContainsKey(key))
                    {
                        _restoreMap[key] = (original, ecoOriginal);
                    }

                    bool demoted = TrySetPriorityClass(pid, NativeMethods.Priority.BelowNormal);
                    if (demoted)
                    {
                        lowered++;
                        // Disable the OS priority boost so background threads
                        // can't grab quantum extensions mid-frame.
                        using (var handle = NativeMethods.Handles.OpenProcess(
                            NativeMethods.ProcessAccess.SetInformation, false, (uint)pid))
                        {
                            if (!handle.IsInvalid)
                                NativeMethods.Priority.SetProcessPriorityBoost(handle, true); // true = disable boost
                        }
                        if (ecoOriginal == false && TrySetEco(pid, true))
                        {
                            ecoCount++;
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }

                string gameName = "?";
                try { gameName = Process.GetProcessById(targetPid).ProcessName; } catch { }

                var exclusions = new List<string>();
                if (protectedPids != null)
                {
                    foreach (int p in protectedPids)
                    {
                        try { exclusions.Add(Process.GetProcessById(p).ProcessName); } catch { }
                    }
                }

                OptimizationSessionOrchestrator.Instance.StartSession(targetPid, gameName, new OptimizationProfile { Aggressiveness = AggressivenessLevel.Light, Exclusions = exclusions });

                // NOTE: no mass background sweep here. The 13:43 capture proved
                // that parking every Normal-priority process (svchosts, driver
                // hosts, MemCompression…) onto a 2-set background pool on the
                // interrupt core causes ~190 ms system stalls (0.1% low 5.3 FPS).
                // Only ACTIVE contenders get demoted: the per-process demotion
                // loop above (BelowNormal + no boost + EcoQoS) and the
                // orchestrator's reactive path handle them one by one. Idle
                // background processes cost the game nothing — High priority
                // preempts them instantly — so leave them alone.

                // Read AFTER StartSession: the booster has applied High by now,
                // so this reports the real before → after transition.
                string targetAfter = ProcessTuningService.PriorityName(ReadPriorityClass(targetPid));
                IsActive = true;

                return new GamingModeResult
                {
                    Success = ok,
                    LoweredCount = lowered,
                    EcoCount = ecoCount,
                    FailedCount = failed,
                    TargetBefore = targetBefore,
                    TargetAfter = targetAfter,
                    Error = ok ? string.Empty : NativeSnapshotService.LastError("Setting priority class failed.").Message,
                };
            }
            catch (Exception ex)
            {
                return new GamingModeResult { Success = false, Error = ex.Message };
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores everything Game Mode changed, in two layers: the orchestrator
    /// session end restores full baselines (game process + threads, throttled
    /// background incl. boost/mem/IO/affinity/CPU Sets), then the local map
    /// restores CPU-bound auto-raises (priority-only changes with no baseline).
    /// Safe to call repeatedly; no-ops when nothing is held.
    /// </summary>
    /// <summary>
    /// Resets every accessible non-critical process's priority class to Normal.
    /// Intended as a panic "undo everything" — covers processes changed by this
    /// app, other tools, or manual tweaks. Skips critical system processes,
    /// itself, and protected processes. Returns (reset, skipped) counts.
    /// </summary>
    public (int Reset, int Skipped) ResetAllPrioritiesToNormal()
    {
        int reset = 0, skipped = 0;
        int self = Environment.ProcessId;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                int pid = proc.Id;
                string name;
                try { name = proc.ProcessName + ".exe"; }
                catch { skipped++; continue; }

                if (pid <= 4 || pid == self
                    || ProcessTuningService.IsCritical(name, pid)
                    || ProcessOptimizer.Services.ProtectedProcessGuard.IsProcessProtected(proc.ProcessName))
                {
                    skipped++;
                    continue;
                }

                if (TrySetPriorityClass(pid, NativeMethods.Priority.Normal)) reset++;
                else skipped++; // includes protected/unopenable processes
            }
            catch { skipped++; }
            finally { try { proc.Dispose(); } catch { } }
        }
        _restoreMap.Clear();
        return (reset, skipped);
    }

    public void Deactivate()
    {
        OptimizationSessionOrchestrator.Instance.EndSession();

        foreach (var kvp in _restoreMap)
        {
            string key = kvp.Key;
            uint priority = kvp.Value.Priority;
            bool? eco = kvp.Value.Eco;

            string[] parts = key.Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int pid)) continue;

            // Verify PID still points to the same instance!
            if (GetProcessKey(pid) != key)
            {
                continue; // Stale PID (process exited and PID reused, or dead)
            }

            TrySetPriorityClass(pid, priority);
            // Re-enable the OS priority boost (disabled during the session).
            using (var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid))
            {
                if (!handle.IsInvalid)
                    NativeMethods.Priority.SetProcessPriorityBoost(handle, false); // false = boost enabled
            }
            if (eco.HasValue)
            {
                TrySetEco(pid, eco.Value);
            }
        }

        _restoreMap.Clear();
        IsActive = false;
    }

    /// <summary>
    /// Instantaneous CPU% estimate for one process, sampled over a short
    /// interval. Cached snapshot pair (two passes ~400 ms apart at activate
    /// time is fine — activation is not latency-critical).
    /// </summary>
    private static double GetCpuPercentSnapshot(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            TimeSpan t1 = p.TotalProcessorTime;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Thread.Sleep(200);
            using var p2 = Process.GetProcessById(pid);
            TimeSpan t2 = p2.TotalProcessorTime;
            sw.Stop();
            if (sw.ElapsedMilliseconds <= 0) return 0;
            return (t2 - t1).TotalMilliseconds / sw.ElapsedMilliseconds * 100.0;
        }
        catch { return 0; }
    }

    private static uint ReadPriorityClass(int pid)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return 0;
            }

            uint cls = NativeMethods.Priority.GetPriorityClass(process);
            return cls == 0 ? 0 : cls;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TrySetPriorityClass(int pid, uint cls)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return false;
            }

            return NativeMethods.Priority.SetPriorityClass(process, cls);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the process's Efficiency mode (EcoQoS) state via
    /// GetProcessInformation. Null when unreadable — callers must not
    /// toggle eco for such processes, because restore would be impossible.
    /// </summary>
    private static bool? ReadEcoState(int pid)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return null;
            }

            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (!NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                return null;
            }

            return (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets Efficiency mode (EcoQoS) for one process. Best effort.</summary>
    private static bool TrySetEco(int pid, bool enabled)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return false;
            }

            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            return NativeMethods.Power.SetProcessInformation(
                process, ProcessInformationClass.ProcessPowerThrottling,
                ref state, NativeMethods.Power.StateSize());
        }
        catch
        {
            return false;
        }
    }
}
