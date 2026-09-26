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
/// Three consecutive samples at >= 50% of one core count as "CPU-bound" -
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

/// <summary>How one <see cref="GamingModeService.AcquireAsync"/> call ended.</summary>
public enum GamingModeOutcome
{
    /// <summary>This call ran the sweep and started a session.</summary>
    Activated,
    /// <summary>A session for the same target was already live; this call only added a hold.</summary>
    JoinedSameTarget,
    /// <summary>A session for another, still-running target was already live; nothing changed.</summary>
    JoinedOtherTarget,
    /// <summary>The session belonged to a process that has exited; it now belongs to this target.</summary>
    HandedOff,
    /// <summary>Activation failed and the hold was rolled back.</summary>
    Failed,
}

/// <summary>
/// One-shot result summary for the Gaming mode toggle, formatted for the status line.
/// </summary>
public sealed class GamingModeResult
{
    public GamingModeOutcome Outcome { get; init; } = GamingModeOutcome.Activated;
    public bool Success { get; init; }
    public int LoweredCount { get; init; }
    public int EcoCount { get; init; }
    public int FailedCount { get; init; }
    /// <summary>How many callers hold the session after this call finished.</summary>
    public int HeldCount { get; init; }
    public string TargetBefore { get; init; } = "?";
    public string TargetAfter { get; init; } = "?";
    public string Error { get; init; } = string.Empty;

    public string Summary => Outcome switch
    {
        GamingModeOutcome.JoinedSameTarget =>
            $"already on for target {TargetAfter} · {HeldCount} holder(s) · {LoweredCount} process(es) still demoted",
        GamingModeOutcome.JoinedOtherTarget =>
            $"a session is already running for {TargetAfter} - {TargetBefore} was left as it is",
        GamingModeOutcome.Failed => $"failed: {Error}",
        _ => $"target {TargetBefore} → {TargetAfter} · {LoweredCount} background lowered · {EcoCount} in Efficiency mode" +
             (FailedCount > 0 ? $" · {FailedCount} skipped (no access)" : ""),
    };
}

/// <summary>
/// Gaming mode for the Threads window. It does exactly two things:
///
/// - the TARGET process goes to AboveNormal with Efficiency mode forced OFF,
///   memory priority 5 and I/O priority Normal;
/// - background processes that were really burning CPU in the activation
///   sample get their priority class lowered to BelowNormal and their
///   Efficiency mode (EcoQoS) turned ON.
///
/// It does NOT partition CPU Sets, clamp affinity, cap Job Objects, rewrite
/// threads or touch the priority-boost flag. Every one of those used to be
/// here and each cost more performance than it returned - see Docs/GameMode.md.
///
/// All original priority classes and eco states are restored when switched
/// off (also on window close, so the system is never left boosted).
///
/// CPU-bound detection is a pure counter state machine fed by the window's
/// 2 s tick: three consecutive samples at >= 50% of one core count as
/// "CPU-bound" - that is the regime where raising priority actually changes
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
    /// A background process must be using at least this share of TOTAL machine
    /// CPU (summed over every logical processor) in the activation sample
    /// before it is demoted. At 2% of a 16-thread CPU that is roughly a third
    /// of one core held continuously - real competition for the game, not a
    /// housekeeping blip.
    /// </summary>
    private const double ContentionPercentOfTotalCpu = 2.0;

    /// <summary>
    /// PID → (original priority class, original Efficiency mode). Eco is null
    /// when the original state could not be read - such processes get their
    /// priority lowered but are never eco-toggled. Restoration targets these.
    /// </summary>
    private readonly Dictionary<string, (uint Priority, bool? Eco)> _restoreMap = new();

    /// <summary>
    /// Who is holding the session open. The session lives while at least one
    /// caller holds it, and the LAST release is what restores everything - see
    /// <see cref="GameModeHoldRegistry"/> for why the first-release-wins shape
    /// this replaced stranded state.
    /// </summary>
    private readonly GameModeHoldRegistry _holds = new();

    /// <summary>True while the session is live (at least one holder).</summary>
    public bool IsActive => _holds.IsHeld;

    /// <summary>Current holds, oldest first - what the UI renders to explain why it is on.</summary>
    public IReadOnlyList<GameModeHold> Holds => _holds.Holds;

    /// <summary>The process the live session belongs to, or null when idle.</summary>
    public int? SessionTargetPid => _holds.SessionTargetPid;

    public bool IsHeldBy(GameModeOwner owner) => _holds.IsHeldBy(owner);

    public bool IsHeldByOther(GameModeOwner owner) => _holds.IsHeldByOther(owner);

    /// <summary>Number of processes we have changed and can restore.</summary>
    public int RestorableCount => _restoreMap.Count;

    /// <summary>Best-effort process name for a hold's label; never throws.</summary>
    private static string NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return $"PID {pid}"; }
    }

    /// <summary>
    /// Whether the session's current target is still running. Handed to the
    /// hold registry, which uses it to decide between joining a live session
    /// and handing the session over to a new target.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

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
    /// raised out of Below Normal/Idle - a throttled process was set there
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
    /// Takes this owner's hold on Gaming mode, activating the session if nobody
    /// else has it yet. Safe to call repeatedly and from anywhere.
    ///
    /// The session is refcounted, so a second caller JOINS rather than re-running
    /// the sweep: a second sweep would snapshot already-demoted processes as
    /// their "original" state and lose the real baseline, and its own release
    /// would then restore that demoted state. Only the LAST release restores.
    /// </summary>
    public async Task<GamingModeResult> AcquireAsync(
        GameModeOwner owner,
        int targetPid,
        IReadOnlyCollection<int>? protectedPids = null,
        string? reason = null)
    {
        protectedPids ??= new[] { targetPid };

        GameModeAcquireResult decision = _holds.Acquire(
            owner, targetPid, NameOf(targetPid), reason ?? owner.ToString(), IsProcessAlive);

        switch (decision.Action)
        {
            case GameModeAcquireAction.Joined:
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.JoinedSameTarget,
                    Success = true,
                    LoweredCount = _restoreMap.Count,
                    HeldCount = _holds.Count,
                    TargetBefore = decision.Hold.TargetName,
                    TargetAfter = decision.Hold.TargetName,
                };

            case GameModeAcquireAction.JoinedOtherTarget:
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.JoinedOtherTarget,
                    Success = true,
                    HeldCount = _holds.Count,
                    TargetBefore = decision.Hold.TargetName,
                    TargetAfter = NameOf(decision.RunningTargetPid ?? targetPid),
                };

            case GameModeAcquireAction.HandOff:
                // The session belonged to a process that has exited. Restore it
                // before pointing the session at the new target.
                TearDown();
                break;
        }

        GamingModeResult result = await ActivateCoreAsync(targetPid, protectedPids).ConfigureAwait(false);

        if (!result.Success)
        {
            // Never leave a hold behind claiming a session that never started, and
            // never leave a half-applied sweep with no holder left to restore it.
            if (_holds.Release(owner).SessionEnded)
            {
                TearDown();
            }
            return result;
        }

        return new GamingModeResult
        {
            Outcome = decision.Action == GameModeAcquireAction.HandOff
                ? GamingModeOutcome.HandedOff
                : GamingModeOutcome.Activated,
            Success = true,
            LoweredCount = result.LoweredCount,
            EcoCount = result.EcoCount,
            FailedCount = result.FailedCount,
            HeldCount = _holds.Count,
            TargetBefore = result.TargetBefore,
            TargetAfter = result.TargetAfter,
        };
    }

    /// <summary>
    /// The one-shot sweep for a fresh session: records every mutable process's
    /// priority class and eco state, raises the target, and demotes the
    /// background processes that were really using CPU. Critical system
    /// processes, this app, exempt processes, and anything we cannot read back
    /// (and therefore could not restore) are never touched.
    /// </summary>
    private Task<GamingModeResult> ActivateCoreAsync(int targetPid, IReadOnlyCollection<int> protectedPids)
    {
        return Task.Run(() =>
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
                // Deactivate could then only restore the boosted values -
                // leaving the game stuck at High after Game Mode turns off.
                bool ok = true;

                // One-shot background demotion: an ordinary Normal-priority
                // process that was really using CPU drops to BelowNormal and
                // gets EcoQoS ON. Strict by design - High/Realtime/
                // AboveNormal (deliberate), Idle/BelowNormal (already low),
                // critical, protected, self, and the game are never touched.
                // The priority-boost flag is left alone; it is the user's
                // permanent per-process preference, not ours to override.
                // Everything recorded here (true originals, pid+startTime
                // keyed) is restored by Deactivate.
                int lowered = 0;
                int ecoCount = 0;
                int failed = 0;
                int selfPid = Process.GetCurrentProcess().Id;

                // ONE machine-wide CPU sample for the whole loop (a 250 ms pass
                // pair), instead of a 200 ms sleep per candidate process.
                Dictionary<int, double> cpu = SampleCpuPercentOfTotal();

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
                    finally
                    {
                        // Released immediately: everything below works off
                        // `name`/`pid`, and Process.GetProcesses() hands back one
                        // disposable object per running process.
                        try { proc.Dispose(); } catch { }
                    }

                    if (pid <= 4 || pid == targetPid || pid == selfPid) continue;
                    if (protectedPids.Contains(pid)) continue;
                     if (ProcessTuningService.IsCritical(name, pid)) continue;
                     if (ProcessTuningService.IsSelf(pid)) continue;

                     // Contention gate: idle processes don't compete with the
                    // game (its AboveNormal class preempts them instantly), so
                    // touching them is pure downside - each write churns the
                    // scheduler and the restore map for zero gain. Only demote
                    // processes that were actually burning CPU in the sample.
                    if (!cpu.TryGetValue(pid, out double cpuPct)) continue;
                    if (cpuPct < ContentionPercentOfTotalCpu) continue;

                    // One handle for the read AND the writes; the old path
                    // opened each candidate process four separate times.
                    using var handle = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation,
                        false, (uint)pid);
                    if (handle.IsInvalid)
                    {
                        failed++; // no access - can't restore later, don't touch
                        continue;
                    }

                    uint original = NativeMethods.Priority.GetPriorityClass(handle);
                    if (original == 0)
                    {
                        failed++; // unreadable - same reasoning, don't touch
                        continue;
                    }
                    if (original != NativeMethods.Priority.Normal) continue; // respect all non-Normal

                    bool? ecoOriginal = ReadEcoState(handle);

                    // Write-once: the first value seen is the true original, and
                    // nothing may overwrite it with an already-demoted state.
                    string key = GetProcessKey(pid);
                    if (!_restoreMap.ContainsKey(key))
                    {
                        _restoreMap[key] = (original, ecoOriginal);
                    }

                    if (NativeMethods.Priority.SetPriorityClass(handle, NativeMethods.Priority.BelowNormal))
                    {
                        lowered++;
                        if (ecoOriginal == false && TrySetEco(handle, true))
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
                foreach (int p in protectedPids)
                {
                    try { exclusions.Add(Process.GetProcessById(p).ProcessName); } catch { }
                }

                OptimizationSessionOrchestrator.Instance.StartSession(targetPid, gameName, new OptimizationProfile { Aggressiveness = AggressivenessLevel.Light, Exclusions = exclusions });

                // NOTE: no mass background sweep here. The 13:43 capture proved
                // that parking every Normal-priority process (svchosts, driver
                // hosts, MemCompression…) onto a 2-set background pool on the
                // interrupt core causes ~190 ms system stalls (0.1% low 5.3 FPS).
                // Only ACTIVE contenders get demoted: the per-process demotion
                // loop above (BelowNormal + no boost + EcoQoS) and the
                // orchestrator's reactive path handle them one by one. Idle
                // background processes cost the game nothing - High priority
                // preempts them instantly - so leave them alone.

                // Read AFTER StartSession: the booster has applied High by now,
                // so this reports the real before → after transition.
                string targetAfter = ProcessTuningService.PriorityName(ReadPriorityClass(targetPid));

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
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.Failed,
                    Success = false,
                    Error = ex.Message,
                };
            }
        });
    }

    /// <summary>
    /// Resets every accessible non-critical process's priority class to Normal.
    /// Intended as a panic "undo everything" - covers processes changed by this
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

    /// <summary>
    /// Drops this owner's hold. Everything is restored only when that was the
    /// LAST hold: a rule going quiet must not tear the session down while the
    /// user's page or the benchmark is still holding it, which is precisely what
    /// the old unconditional Deactivate did.
    /// </summary>
    /// <returns>True when the session ended and state was restored.</returns>
    public bool Release(GameModeOwner owner)
    {
        GameModeReleaseResult result = _holds.Release(owner);
        if (result.SessionEnded)
        {
            TearDown();
        }
        return result.SessionEnded;
    }

    /// <summary>
    /// Drops every hold and restores immediately, whoever took them. For the
    /// "Restore" button and for app shutdown, where ending the session is the
    /// point rather than a side effect.
    ///
    /// Restores unconditionally rather than only when a hold existed: CPU-bound
    /// auto-raise writes to the same restore map without taking a hold, so
    /// gating this on holds would leave the button enabled (RestorableCount > 0)
    /// and then do nothing.
    /// </summary>
    public void ReleaseAll()
    {
        _holds.Clear();
        TearDown();
    }

    /// <summary>
    /// The restore half of a session end, in two layers: the orchestrator
    /// session end restores full baselines (game process, throttled background
    /// incl. memory/IO), then the local map restores CPU-bound auto-raises
    /// (priority + eco changes with no baseline). Safe to call when nothing is
    /// held; restores nothing it did not change.
    /// </summary>
    private void TearDown()
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

            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (handle.IsInvalid) continue;

            NativeMethods.Priority.SetPriorityClass(handle, priority);

            // The priority-boost flag is deliberately NOT restored here. This
            // path never changed it - only the priority class and EcoQoS were
            // demoted - and the old unconditional "re-enable boost" silently
            // reverted a per-process "boost disabled" preference the user had
            // set, which the 20 s keeper then turned back off. The two systems
            // fought forever and the setting looked like it kept resetting.
            if (eco.HasValue)
            {
                TrySetEco(handle, eco.Value);
            }
        }

        _restoreMap.Clear();
    }

    /// <summary>
    /// CPU usage of every running process, as a percentage of TOTAL machine CPU
    /// (0-100), from ONE pass pair: read everyone's kernel+user times, wait
    /// once, read again, diff.
    ///
    /// The old shape was a per-PID helper that slept 200 ms inside itself and
    /// was called once per candidate process - on a machine with a couple of
    /// hundred processes that was ~40 seconds of serial sleeping inside
    /// ActivateAsync, with the box churning for the whole first minute of the
    /// game it was meant to be helping. This costs one 250 ms sleep in total.
    /// </summary>
    private static Dictionary<int, double> SampleCpuPercentOfTotal(int sampleMs = 250)
    {
        var usage = new Dictionary<int, double>();
        Dictionary<int, long> first = ReadAllCpuTicks();
        if (first.Count == 0) return usage;

        var sw = Stopwatch.StartNew();
        Thread.Sleep(sampleMs);
        Dictionary<int, long> second = ReadAllCpuTicks();
        sw.Stop();

        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        if (elapsedMs <= 0) return usage;

        int cpus = Math.Max(1, Environment.ProcessorCount);
        foreach (var kvp in second)
        {
            if (!first.TryGetValue(kvp.Key, out long before)) continue; // born mid-sample
            long delta = kvp.Value - before;
            if (delta <= 0) continue;
            // 10 000 ticks = 1 ms; share of wall time, then of the whole machine.
            usage[kvp.Key] = delta / 10_000.0 / elapsedMs * 100.0 / cpus;
        }
        return usage;
    }

    /// <summary>
    /// Kernel+user CPU time per PID in 100 ns ticks. One handle at a time, each
    /// opened and released inside the loop, so a sample never holds more than a
    /// single process open.
    /// </summary>
    private static Dictionary<int, long> ReadAllCpuTicks()
    {
        var ticks = new Dictionary<int, long>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return ticks; }

        foreach (Process proc in procs)
        {
            try
            {
                int pid = proc.Id;
                if (pid <= 4) continue;
                using var handle = NativeMethods.Handles.OpenProcess(
                    NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
                if (handle.IsInvalid) continue;
                if (NativeMethods.Times.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                {
                    ticks[pid] = kernel.ToTicks() + user.ToTicks();
                }
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return ticks;
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
    /// GetProcessInformation. Null when unreadable - callers must not
    /// toggle eco for such processes, because restore would be impossible.
    /// </summary>
    private static bool? ReadEcoState(SafeProcessHandle process)
    {
        try
        {
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
    private static bool TrySetEco(SafeProcessHandle process, bool enabled)
    {
        try
        {
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
