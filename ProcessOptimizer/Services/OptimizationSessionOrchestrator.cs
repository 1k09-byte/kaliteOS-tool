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

    public OptimizationSessionState CurrentState => _state;
    public IEnumerable<ManagedProcessEntry> GetManagedProcesses() => _managed.Values.ToList();

    public event EventHandler? StateChanged;

    private OptimizationSessionOrchestrator()
    {
        _monitor = new ContentionMonitor();
        _monitor.ContentionDetected += OnContentionDetected;
    }

    public void StartSession(int gamePid, string gameName, OptimizationProfile profile)
    {
        lock (_lock)
        {
            if (_state.IsActive) return;

            _currentProfile = profile ?? new OptimizationProfile();
            _state = new OptimizationSessionState
            {
                IsActive = true,
                ActiveGamePid = gamePid,
                ActiveGameName = gameName,
                ProfileName = "Session Optimizer", // placeholder until dynamic maps are wired
                SessionStartTime = DateTime.UtcNow,
                ProcessesManaged = 0,
                ProcessesThrottled = 0,
                ProcessesUnchanged = 0
            };

            _monitor.StartMonitoring();

            // Snapshot the game's original state BEFORE we boost it
            var gameSnapshot = ProcessStateSnapshotService.CaptureSnapshot(gamePid);
            if (gameSnapshot != null)
            {
                _baselines[gamePid] = gameSnapshot;
            }

            // Snapshot live game threads BEFORE the booster rewrites them
            // (priority, boost, Eco, memory, ideal processor, CPU Sets).
            try { _gameThreads[gamePid] = ProcessStateSnapshotService.CaptureThreadSnapshots(gamePid); }
            catch { }

            // Apply true SMT-bypassing FPS boost to the game
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

            // Restore game threads the booster rewrote (process-level
            // restore runs first so defaults are back before thread pins).
            foreach (var kvp in _gameThreads)
            {
                try { ProcessStateSnapshotService.RestoreThreadSnapshots(kvp.Key, kvp.Value.StartTicks, kvp.Value.Threads); }
                catch { }
            }

            _baselines.Clear();
            _gameThreads.Clear();
            _managed.Clear();
            _hysteresisCounts.Clear();
            
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

            // Drop hysteresis counters for processes that no longer exist, so a PID
            // reused later in the session starts from zero instead of inheriting a
            // dead process's heat.
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

                // Skip active game
                if (pid == _state.ActiveGamePid) continue;

                // Never throttle critical / protected / exempt processes or self.
                string procName = GetProcessNameSafe(pid);
                if (kaliteConfig.Services.ProcessTuningService.IsCritical(procName + ".exe", pid)
                    || kaliteConfig.Services.ProcessTuningService.IsSelf(pid)
                    || kaliteConfig.Services.GamingExemptionService.IsExempt(procName)
                    || ProtectedProcessGuard.IsProcessProtected(procName, _currentProfile.Exclusions))
                    continue;

                // Escalate on SUSTAINED heat only: the first busy sample is never
                // punished, which is what protects 1% lows from a momentary spike.
                int hits = _hysteresisCounts.AddOrUpdate(pid, 1, (_, v) => v + 1);
                if (!ContentionPolicy.ShouldThrottle(hits)) continue;

                // Moderate is the ceiling (no Aggressive tier: its Job Object rate
                // cap could never be lifted off a process mid-session).
                AggressivenessLevel targetLevel = ContentionPolicy.EscalationFor(hits);

                // Cap at the maximum aggressiveness allowed by the current profile
                if (targetLevel > _currentProfile.Aggressiveness)
                    targetLevel = _currentProfile.Aggressiveness;

                // If already managed, just update the signal visually and possibly escalate
                if (_managed.TryGetValue(pid, out var existing))
                {
                    existing.LastContentionSignal = cpu;
                    existing.QuietTicks = 0; // hot again: the calm streak is over
                    
                    if (existing.CurrentThrottleLevel != targetLevel)
                    {
                        existing.CurrentThrottleLevel = targetLevel;
                        existing.ActionTaken = BackgroundThrottleService.ApplyThrottle(pid, targetLevel);
                        stateChanged = true;
                    }
                    continue;
                }

                // Respect manually-elevated High/Realtime: only manage Normal
                // and AboveNormal processes for priority demotion.
                var snapshot = ProcessStateSnapshotService.CaptureSnapshot(pid, _currentProfile.Exclusions);
                if (snapshot == null) continue; // Protected or exited
                if (snapshot.OriginalPriorityClass is 0x00000080 or 0x00000100) // High / Realtime
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

            // Release pass: SUSTAINED calm plus a minimum dwell. The old rule gave a
            // process back the moment it missed a single sample, so anything sitting
            // near the threshold was demoted and restored over and over — and each
            // restore is a full priority/eco/memory/IO pass, which costs more than
            // the contention it was reacting to.
            DateTime now = DateTime.UtcNow;
            var release = new List<int>();
            foreach (var kvp in _managed)
            {
                if (contentionPids.ContainsKey(kvp.Key)) continue; // hot: reset in the loop above

                kvp.Value.QuietTicks++;
                if (ContentionPolicy.ShouldForgetHeat(kvp.Value.QuietTicks))
                {
                    // Calm long enough not to count as sustained any more: the ladder
                    // restarts from Light on the next surge instead of resuming at
                    // Moderate. The process stays demoted until the dwell is up.
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
                // Push update to UI
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
