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
using System.Runtime.InteropServices;
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
    /// <summary>Background processes whose Priority boost flag was switched off.</summary>
    public int BoostStrippedCount { get; init; }
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
             (BoostStrippedCount > 0 ? $" · {BoostStrippedCount} boost disabled" : "") +
             (FailedCount > 0 ? $" · {FailedCount} skipped (no access)" : ""),
    };
}

/// <summary>
/// Gaming mode: raises the target process, and demotes CPU-burning background
/// processes (priority class, priority boost, EcoQoS) while restoring
/// everything on the way out. The whole game family - target, descendants and
/// same-name siblings - is excluded from the demotion.
/// </summary>
public sealed class GamingModeService
{
    /// <summary>Share of TOTAL machine CPU a process must burn to be demoted.</summary>
    private const double ContentionPercentOfTotalCpu = 2.0;

    /// <summary>A process's pre-session state; a null field means never read, so never written back.</summary>
    private sealed record RestoreEntry
    {
        public uint Priority { get; init; }
        public bool? Eco { get; init; }
        /// <summary>Original Priority-boost flag, inverted as the Win32 API is.</summary>
        public bool? BoostDisabled { get; init; }
    }

    /// <summary>"pid_startTime" → pre-session state, so a reused PID is never restored into.</summary>
    private readonly Dictionary<string, RestoreEntry> _restoreMap = new();

    private void Remember(string key, uint priority, bool? eco, bool? boostDisabled)
    {
        if (!_restoreMap.ContainsKey(key))
            _restoreMap[key] = new RestoreEntry { Priority = priority, Eco = eco, BoostDisabled = boostDisabled };
    }

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
    /// PIDs that must never be demoted: the target, the other protected pids,
    /// their descendants, and same-name siblings. Excluding only the clicked pid
    /// is what let a game's own helpers and second instances be demoted.
    /// </summary>
    private static HashSet<int> BuildProtectedFamily(int targetPid, IReadOnlyCollection<int> protectedPids)
    {
        var family = new HashSet<int>();
        if (protectedPids != null)
        {
            foreach (int p in protectedPids) family.Add(p);
        }
        family.Add(targetPid);

        var parentOf = ReadParentPids();
        if (parentOf.Count == 0) return family;

        // Repeat to a fixed point so grandchildren are covered too.
        var roots = new HashSet<int>(family);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var kvp in parentOf)
            {
                if (family.Contains(kvp.Key)) continue;
                if (!roots.Contains(kvp.Value)) continue;
                family.Add(kvp.Key);
                grew = true;
            }
        }

        // Extra instances are started by the launcher or an updater, so they are not
        // always descendants of the selected pid.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in family)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                names.Add(p.ProcessName);
            }
            catch { }
        }
        if (names.Count > 0)
        {
            foreach (var kvp in parentOf) // same live-PID universe as the snapshot
            {
                if (family.Contains(kvp.Key)) continue;
                try
                {
                    using var p = Process.GetProcessById(kvp.Key);
                    if (names.Contains(p.ProcessName)) family.Add(kvp.Key);
                }
                catch { }
            }
        }

        return family;
    }

    /// <summary>One Toolhelp32 pass returning PID → parent PID, including processes that deny query rights.</summary>
    private static Dictionary<int, int> ReadParentPids()
    {
        var map = new Dictionary<int, int>(512);
        try
        {
            IntPtr snapshot = NativeMethods.Toolhelp.CreateToolhelp32Snapshot(
                NativeMethods.Toolhelp.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
            try
            {
                var entry = new NativeMethods.Toolhelp.PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.PROCESSENTRY32>()
                };
                if (NativeMethods.Toolhelp.Process32First(snapshot, ref entry))
                {
                    do
                    {
                        map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                        entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.PROCESSENTRY32>();
                    }
                    while (NativeMethods.Toolhelp.Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                NativeMethods.Handles.CloseHandle(snapshot);
            }
        }
        catch
        {
            map.Clear();
        }
        return map;
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

            // Record the pre-change value so a later restore puts things back.
            Remember(GetProcessKey(pid), current, null, null);

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
            BoostStrippedCount = result.BoostStrippedCount,
            FailedCount = result.FailedCount,
            HeldCount = _holds.Count,
            TargetBefore = result.TargetBefore,
            TargetAfter = result.TargetAfter,
        };
    }

    /// <summary>
    /// The one-shot sweep for a fresh session: records every mutable process's
    /// priority class, boost flag and eco state; raises the target; and demotes
    /// the background processes that were really using CPU (priority class →
    /// BelowNormal, priority boost off, EcoQoS on).
    ///
    /// Never touched: critical system processes, this app, gaming-exempt
    /// processes (launchers / overlays / audio / anti-cheat), the whole game
    /// family (see <see cref="BuildProtectedFamily"/>), and anything we cannot
    /// read back and would therefore be unable to restore.
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
                // process that was really using CPU drops to BelowNormal, has
                // its priority BOOST switched off, and gets EcoQoS ON.
                //
                // Boost matters as much as the class: with the flag still set
                // the scheduler treats those threads as boost-eligible and hands
                // back the quantum they just yielded, so the demotion appears to
                // do nothing.
                //
                // Strict by design - High/Realtime/AboveNormal (deliberate),
                // Idle/BelowNormal (already low), critical, protected, self,
                // exempt (launchers/overlays/audio/anti-cheat), and the entire
                // game family are never touched.
                int lowered = 0;
                int ecoCount = 0;
                int boostStripped = 0;
                int failed = 0;
                int selfPid = Process.GetCurrentProcess().Id;

                // Guard the whole family, not just the clicked pid: a launcher, a helper or a
                // second instance of the game would otherwise be demoted.
                var gameFamily = BuildProtectedFamily(targetPid, protectedPids);

                // ONE machine-wide CPU sample for the whole loop.
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

                    if (pid <= 4 || pid == selfPid) continue;
                    if (gameFamily.Contains(pid)) continue;
                    if (ProcessTuningService.IsCritical(name, pid)) continue;
                    if (ProcessTuningService.IsSelf(pid)) continue;
                    // The reactive path already honours the exemption list; the one-shot
                    // sweep must too, or launchers and anti-cheat get demoted here.
                    if (GamingExemptionService.IsExempt(name)) continue;

                     // Contention gate: idle processes cost nothing to leave alone.
                    if (!cpu.TryGetValue(pid, out double cpuPct)) continue;
                    if (cpuPct < ContentionPercentOfTotalCpu) continue;

                    // One handle for the read AND the writes.
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
                    bool? boostOriginal = ReadBoostDisabled(handle);

                    // Write-once: the first value seen is the true original, and
                    // nothing may overwrite it with an already-demoted state.
                    Remember(GetProcessKey(pid), original, ecoOriginal, boostOriginal);

                    if (NativeMethods.Priority.SetPriorityClass(handle, NativeMethods.Priority.BelowNormal))
                    {
                        lowered++;

                        // Per thread as well as process-wide: the cascade only reaches threads
                        // that already exist, and a thread keeping its boost flag
                        // keeps stealing quanta back.
                        if (boostOriginal == false && TryDisableBoost(pid))
                            boostStripped++;

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

                // Names the orchestrator's reactive path must also exempt. It matches with
                // Contains(), so the target's own name is listed first.
                var exclusions = new List<string>();
                if (!string.IsNullOrWhiteSpace(gameName)) exclusions.Add(gameName);
                foreach (int p in gameFamily)
                {
                    if (p == targetPid) continue;
                    try
                    {
                        string n = Process.GetProcessById(p).ProcessName;
                        if (!string.IsNullOrWhiteSpace(n) && !exclusions.Contains(n)) exclusions.Add(n);
                    }
                    catch { }
                }

                OptimizationSessionOrchestrator.Instance.StartSession(targetPid, gameName,
                    new OptimizationProfile { Aggressiveness = AggressivenessLevel.Light, Exclusions = exclusions },
                    gameFamily);

                // No mass background sweep: only ACTIVE contenders get demoted, which is what
                // the loop above and the orchestrator's reactive path do.

                // Read AFTER StartSession, which has already boosted the target.
                string targetAfter = ProcessTuningService.PriorityName(ReadPriorityClass(targetPid));

                return new GamingModeResult
                {
                    Success = ok,
                    LoweredCount = lowered,
                    EcoCount = ecoCount,
                    BoostStrippedCount = boostStripped,
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
        // A full reset invalidates the boost ledger too, or a later session would
        // restore boost onto state this just overwrote.
        _restoreMap.Clear();
        _threadBoostOriginals.Clear();
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
            RestoreEntry entry = kvp.Value;
            string key = kvp.Key;

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

            NativeMethods.Priority.SetPriorityClass(handle, entry.Priority);

            if (entry.BoostDisabled.HasValue)
            {
                // Restore the value read at demotion time, never an unconditional
                // "re-enable" - that would revert a boost-off preference the user set.
                try
                {
                    NativeMethods.Priority.SetProcessPriorityBoost(handle, entry.BoostDisabled.Value);
                    if (entry.BoostDisabled.Value == false) RestoreThreadBoosts(pid, key);
                }
                catch { }
            }

            if (entry.Eco.HasValue)
            {
                TrySetEco(handle, entry.Eco.Value);
            }
        }

        _restoreMap.Clear();
        // Whatever is left belongs to a process that exited before its turn.
        _threadBoostOriginals.Clear();
    }

    /// <summary>Per-thread boost originals, keyed "pid_startTicks_tid" so a reused PID is never restored into.</summary>
    private readonly Dictionary<string, bool> _threadBoostOriginals = new();

    /// <summary>Disables priority boost process-wide and per thread, recording originals first.</summary>
    private bool TryDisableBoost(int pid)
    {
        // Read every thread's original BEFORE any write: the process-wide
        // disable cascades, so a later read reports "already off" for all of
        // them and the true values are lost.
        var originals = new List<(uint Tid, bool WasDisabled)>();
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                uint tid = (uint)t.Id;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.QueryInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    if (NativeMethods.Priority.GetThreadPriorityBoost(hThread, out bool wasDisabled))
                        originals.Add((tid, wasDisabled));
                }
                catch { }
            }
        }
        catch { /* per-row originals are best effort; the process write still runs */ }

        bool ok = false;
        try
        {
            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (!handle.IsInvalid)
                ok = NativeMethods.Priority.SetProcessPriorityBoost(handle, Svetlana: true);
        }
        catch { }

        // Persist the originals, then write each thread: the cascade only reaches
        // the threads that existed, so anything created since needs its own write.
        try
        {
            using var proc = Process.GetProcessById(pid);
            long ticks;
            try { ticks = proc.StartTime.Ticks; } catch { return ok; }
            foreach (var (tid, wasDisabled) in originals)
            {
                string key = $"{pid}_{ticks}_{tid}";
                if (!_threadBoostOriginals.ContainsKey(key)) _threadBoostOriginals[key] = wasDisabled;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    NativeMethods.Priority.SetThreadPriorityBoost(hThread, Svetlana: true);
                }
                catch { }
            }
        }
        catch { }

        return ok;
    }

    /// <summary>
    /// Puts per-thread boost back the way it was, for the exact process
    /// instance. Only threads recorded as boost-enabled are touched: a thread
    /// the user had already switched off is left off.
    /// </summary>
    private void RestoreThreadBoosts(int pid, string processKey)
    {
        try
        {
            string prefix = processKey + "_";
            foreach (var kvp in _threadBoostOriginals)
            {
                if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!uint.TryParse(kvp.Key.Substring(prefix.Length), out uint tid)) continue;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    if (NativeMethods.Priority.SetThreadPriorityBoost(hThread, kvp.Value))
                        _threadBoostOriginals.Remove(kvp.Key);
                }
                catch { }
            }
        }
        catch { }
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
    /// Reads the process's Priority-boost flag, in the API's inverted form
    /// (true = boost is OFF). Null when unreadable - callers must not write it
    /// back for such processes, because restore would be impossible.
    /// </summary>
    private static bool? ReadBoostDisabled(SafeProcessHandle process)
    {
        try
        {
            if (process.IsInvalid) return null;
            return NativeMethods.Priority.GetProcessPriorityBoost(process, out bool boostDisabled)
                ? boostDisabled
                : null;
        }
        catch
        {
            return null;
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
