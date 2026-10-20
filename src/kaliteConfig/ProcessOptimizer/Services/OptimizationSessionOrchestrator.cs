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
using System.Threading.Tasks;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

public class OptimizationSessionOrchestrator : IDisposable
{
    private static readonly OptimizationSessionOrchestrator _instance = new();
    public static OptimizationSessionOrchestrator Instance => _instance;

    private readonly IContentionMonitor _monitor;
    private readonly ConcurrentDictionary<int, ProcessBaselineSnapshot> _baselines = new();
    private readonly ConcurrentDictionary<int, (long StartTicks, List<GameThreadSnapshot> Threads)> _gameThreads = new();
    private readonly ConcurrentDictionary<int, ManagedProcessEntry> _managed = new();
    private readonly ConcurrentDictionary<int, int> _hysteresisCounts = new();

    private OptimizationSessionState _state = new();
    private OptimizationProfile _currentProfile = new();
    private readonly object _lock = new();

    private readonly HashSet<int> _protectedPids = new();

    public OptimizationSessionState CurrentState => _state;
    public IEnumerable<ManagedProcessEntry> GetManagedProcesses() => _managed.Values.ToList();

    public event EventHandler? StateChanged;

    private OptimizationSessionOrchestrator()
    {
        _monitor = new ContentionMonitor();
        _monitor.ContentionDetected += OnContentionDetected;
    }

    public void StartSession(
        int gamePid,
        string gameName,
        OptimizationProfile profile,
        IEnumerable<int>? protectedPids = null)
    {
        lock (_lock)
        {
            if (_state.IsActive) return;

            _currentProfile = profile ?? new OptimizationProfile();
            _protectedPids.Clear();
            if (protectedPids != null)
            {
                foreach (int pid in protectedPids) _protectedPids.Add(pid);
            }
            _protectedPids.Add(gamePid);
            _state = new OptimizationSessionState
            {
                IsActive = true,
                ActiveGamePid = gamePid,
                ActiveGameName = gameName,
                ProfileName = "Session Optimizer",
                SessionStartTime = DateTime.UtcNow,
                ProcessesManaged = 0,
                ProcessesThrottled = 0,
                ProcessesUnchanged = 0
            };

            _monitor.StartMonitoring();

            var gameSnapshot = ProcessStateSnapshotService.CaptureSnapshot(gamePid);
            if (gameSnapshot != null)
            {
                _baselines[gamePid] = gameSnapshot;
            }

            try { _gameThreads[gamePid] = ProcessStateSnapshotService.CaptureThreadSnapshots(gamePid); }
            catch { }

            ForegroundBoosterService.ApplyOptimization(gamePid);

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void EndSession()
    {
        lock (_lock)
        {
            if (!_state.IsActive) return;

            _monitor.StopMonitoring();

            foreach (var kvp in _baselines)
            {
                ProcessStateSnapshotService.RestoreSnapshot(kvp.Value);
            }

            foreach (var kvp in _gameThreads)
            {
                try { ProcessStateSnapshotService.RestoreThreadSnapshots(kvp.Key, kvp.Value.StartTicks, kvp.Value.Threads); }
                catch { }
            }

            _baselines.Clear();
            _gameThreads.Clear();
            _managed.Clear();
            _hysteresisCounts.Clear();
            _protectedPids.Clear();
            
            _state = new OptimizationSessionState { IsActive = false };
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnContentionDetected(object? sender, ContentionSample sample)
    {
        var contentionPids = sample.HotCpuPercent;

        lock (_lock)
        {
            if (!_state.IsActive) return;

            bool stateChanged = false;

            if (!_hysteresisCounts.IsEmpty)
            {
                foreach (int pid in _hysteresisCounts.Keys)
                {
                    if (!sample.AlivePids.Contains(pid)) _hysteresisCounts.TryRemove(pid, out _);
                }
            }

            foreach (var kvp in contentionPids)
            {
                int pid = kvp.Key;
                double cpu = kvp.Value;

                if (_protectedPids.Contains(pid)) continue;

                string procName = GetProcessNameSafe(pid);
                if (kaliteConfig.Services.ProcessTuningService.IsCritical(procName + ".exe", pid)
                    || kaliteConfig.Services.ProcessTuningService.IsSelf(pid)
                    || kaliteConfig.Services.GamingExemptionService.IsExempt(procName)
                    || ProtectedProcessGuard.IsProcessProtected(procName, _currentProfile.Exclusions))
                    continue;

                int hits = _hysteresisCounts.AddOrUpdate(pid, 1, (_, v) => v + 1);
                if (!ContentionPolicy.ShouldThrottle(hits)) continue;

                AggressivenessLevel targetLevel = ContentionPolicy.EscalationFor(hits);

                if (targetLevel > _currentProfile.Aggressiveness)
                    targetLevel = _currentProfile.Aggressiveness;

                if (_managed.TryGetValue(pid, out var existing))
                {
                    existing.LastContentionSignal = cpu;
                    existing.QuietTicks = 0;
                    
                    if (existing.CurrentThrottleLevel != targetLevel)
                    {
                        existing.CurrentThrottleLevel = targetLevel;
                        existing.ActionTaken = BackgroundThrottleService.ApplyThrottle(pid, targetLevel);
                        stateChanged = true;
                    }
                    continue;
                }

                var snapshot = ProcessStateSnapshotService.CaptureSnapshot(pid, _currentProfile.Exclusions);
                if (snapshot == null) continue;
                if (snapshot.OriginalPriorityClass is 0x00000080 or 0x00000100)
                    continue;

                _baselines[pid] = snapshot;

                var entry = new ManagedProcessEntry
                {
                    Pid = pid,
                    ProcessName = GetProcessNameSafe(pid),
                    ManagedSince = DateTime.UtcNow,
                    Reason = $"Contention ({cpu:F1}% CPU)",
                    LastContentionSignal = cpu,
                    CurrentThrottleLevel = targetLevel
                };

                entry.ActionTaken = BackgroundThrottleService.ApplyThrottle(pid, targetLevel);
                
                _managed[pid] = entry;

                _state.ProcessesThrottled++;
                _state.ProcessesManaged++;
                stateChanged = true;
            }

            DateTime now = DateTime.UtcNow;
            var release = new List<int>();
            foreach (var kvp in _managed)
            {
                if (contentionPids.ContainsKey(kvp.Key)) continue;

                kvp.Value.QuietTicks++;
                if (ContentionPolicy.ShouldForgetHeat(kvp.Value.QuietTicks))
                {
                    _hysteresisCounts.TryRemove(kvp.Key, out _);
                }

                double managedSeconds = (now - kvp.Value.ManagedSince).TotalSeconds;
                if (ContentionPolicy.ShouldRelease(kvp.Value.QuietTicks, managedSeconds))
                {
                    release.Add(kvp.Key);
                }
            }

            foreach (int q in release)
            {
                if (_managed.TryRemove(q, out _))
                {
                    if (_baselines.TryRemove(q, out var baseline))
                    {
                        ProcessStateSnapshotService.RestoreSnapshot(baseline);
                    }
                    _hysteresisCounts.TryRemove(q, out _);
                    _state.ProcessesThrottled--;
                    _state.ProcessesManaged--;
                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private string GetProcessNameSafe(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch { return $"PID {pid}"; }
    }

    public void Dispose()
    {
        EndSession();
    }
}
