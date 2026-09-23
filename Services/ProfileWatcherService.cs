using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>Outcome of one apply pass over a single process.</summary>
public sealed class RuleApplyResult
{
    public bool ProcessFound { get; init; }
    public int Attempted { get; init; }
    public int Succeeded { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>Live matched-thread count per thread rule, in rule order.</summary>
    public List<int> ThreadTargets { get; init; } = new();
}

/// <summary>
/// Background WMI watcher for Win32_ProcessStartTrace that intercepts process launches
/// and maps them against saved JSON rules, applying Priority, Boost, Efficiency,
/// Affinity, CPU Sets and per-thread priorities. Every action is applied and
/// verified independently; the outcome lands in <c>TunerProfile.LastResult</c>.
/// Mutations of bound profiles always happen on the UI dispatcher.
/// </summary>
public sealed class ProfileWatcherService : IDisposable
{
    private readonly ProcessTuningService _tuning;
    private readonly CpuSetService _cpuSets;
    private readonly ThreadTuningService _threads;
    private readonly DispatcherQueue? _dispatcher;
    private readonly object _lock = new();
    private ManagementEventWatcher? _watcher;
    private string _profilePath;
    private string _logPath;

    /// <summary>
    /// PIDs this watcher auto-boosted via Gaming mode rules (launch events).
    /// When the LAST one exits, Gaming mode is deactivated so the machine
    /// never stays lowered while nothing game-related is running.
    /// </summary>
    private readonly HashSet<int> _gamingModePids = new();
    private readonly object _gamingLock = new();
    private System.Threading.Timer? _gamingLivenessTimer;

    public List<TunerProfile> ActiveProfiles { get; private set; } = new();

    /// <summary>Raised on the UI thread after rules change or an apply pass updates results.</summary>
    public event EventHandler? RulesChanged;

    public ProfileWatcherService(ProcessTuningService tuning, CpuSetService cpuSets, ThreadTuningService threads, string? profilePath = null)
    {
        _tuning = tuning;
        _cpuSets = cpuSets;
        _threads = threads;
        try
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcher = null;
        }
        _profilePath = profilePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "threadtuner-profiles.json");
        _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "rules-debug.log");
    }

    /// <summary>Temporary diagnostics for the "rules don't stick" report. Appends, never throws.</summary>
    internal void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            try
            {
                if (new FileInfo(_logPath).Length > 200 * 1024)
                {
                    File.Delete(_logPath);
                }
            }
            catch
            {
            }
            File.AppendAllText(_logPath,
                $"{DateTime.Now:HH:mm:ss.fff} [pid={Environment.ProcessId}] {message}\r\n");
        }
        catch
        {
        }
    }

    public async Task LoadProfilesAsync()
    {
        try
        {
            if (File.Exists(_profilePath))
            {
                var text = await File.ReadAllTextAsync(_profilePath);
                Log($"load: {text.Length} bytes from {_profilePath}");
                var loaded = JsonSerializer.Deserialize<List<TunerProfile>>(text);
                Log($"load: deserialized {(loaded == null ? "null" : loaded.Count.ToString())} profiles");
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        ActiveProfiles = loaded;
                    }
                }
            }
            else
            {
                Log("load: no file yet, generating built-in defaults");
                lock (_lock)
                {
                    ActiveProfiles = CreateBuiltInDefaults();
                }
                _ = SaveProfilesAsync();
            }
        }
        catch (Exception ex)
        {
            Log("load FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
        await EnsureBuiltInDefaultsAsync();
        RaiseChanged();
    }

    private List<TunerProfile> CreateBuiltInDefaults()
    {
        return new List<TunerProfile>
        {
            new TunerProfile
            {
                // Scoped to DWM: generic "Input"/"Sensor"/"Kernel" contains-
                // matches against Pattern "*" never matched a real thread name
                // anywhere else, so the rule sat at "No actions defined". DWM
                // names its role threads (Master Input, Kernel Sensor, ...),
                // and the resolver classifies them exactly like this.
                Name = "Input/Sensor Threads",
                Pattern = "dwm.exe",
                Enabled = true,
                AutoApply = true,
                ThreadRules = new ObservableCollection<TunerThreadRule>
                {
                    new TunerThreadRule
                    {
                        Description = "Master Input",
                        Priority = (int)ThreadPriorityLevel.TimeCritical,
                        // Input threads must never be parked on efficient cores:
                        // EcoQoS here adds pointer-latency spikes.
                        EfficiencyMode = false
                    },
                    new TunerThreadRule
                    {
                        Description = "Kernel Sensor",
                        Priority = (int)ThreadPriorityLevel.TimeCritical,
                        EfficiencyMode = false
                    }
                }
            }
        };
    }

    /// <summary>Ships the built-in defaults to EVERYONE: adds them when missing
    /// and refreshes them when they still carry previously-shipped content.
    /// User-customized copies (anything else) are left untouched, and the
    /// Enabled toggle is always respected.</summary>
    public async Task EnsureBuiltInDefaultsAsync()
    {
        bool changed = false;
        var defaults = CreateBuiltInDefaults();
        lock (_lock)
        {
            // "DWM & Master Input" was retired from the defaults; clean up
            // copies still carrying shipped content so it disappears instead
            // of lingering in every saved profile forever.
            for (int i = ActiveProfiles.Count - 1; i >= 0; i--)
            {
                if (ActiveProfiles[i].Name == "DWM & Master Input" && IsOldDwmMasterInputShipment(ActiveProfiles[i]))
                {
                    Log($"defaults: removed retired built-in 'DWM & Master Input'");
                    ActiveProfiles.RemoveAt(i);
                    changed = true;
                }
            }

            foreach (var def in defaults)
            {
                var existing = ActiveProfiles.FirstOrDefault(p => p.Name == def.Name);
                if (existing == null)
                {
                    ActiveProfiles.Add(def);
                    Log($"defaults: added missing built-in '{def.Name}'");
                    changed = true;
                }
                else if (IsOldShippedContent(existing))
                {
                    existing.Pattern = def.Pattern;
                    existing.PriorityClass = def.PriorityClass;
                    existing.AutoApply = def.AutoApply;
                    existing.ThreadRules = def.ThreadRules;
                    Log($"defaults: refreshed built-in '{def.Name}' to highest-priority content");
                    changed = true;
                }
                else if (RulePatternGuard.RepairPattern(existing.Pattern, existing.Name, def.Pattern) is { } repaired)
                {
                    // The saved copy carries its own display name as the process
                    // pattern ("Input/Sensor Threads" instead of "dwm.exe"), so
                    // FindPids could never match a process and the rule sat at
                    // "Waiting for process" forever — the reason the built-in
                    // DWM Master Input / Kernel Sensor rule never applied.
                    // Only the pattern is repaired; every other user tweak on
                    // the rule (thread affinities, boost flags…) is preserved.
                    Log($"defaults: repaired pattern for built-in '{existing.Name}' " +
                        $"([{existing.Pattern}] → [{repaired}])");
                    existing.Pattern = repaired;
                    changed = true;
                }
            }
        }
        if (changed)
        {
            await SaveProfilesAsync();
            RaiseChanged();
        }
    }

    /// <summary>
    /// True when a rule's "process pattern" is its own display name, so it can
    /// never match anything. Built-ins are repaired at load
    /// (<see cref="RulePatternGuard.RepairPattern"/>); user rules are only
    /// reported in the UI, never silently rewritten.
    /// </summary>
    internal static bool PatternIsDisplayName(TunerProfile p) =>
        RulePatternGuard.IsRuleNameUsedAsPattern(p.Pattern, p.Name);

    private static bool IsOldShippedContent(TunerProfile p)
    {
        if (p.Name == "DWM & Master Input")
        {
            return IsOldDwmMasterInputShipment(p);
        }
        if (p.Name == "Input/Sensor Threads")
        {
            // v1 shipped "*" + Input/Highest + Sensor/AboveNormal; v2 shipped
            // "*" + Input/Sensor/Kernel all Highest; v3 shipped "dwm.exe" +
            // Master Input/Kernel Sensor at Highest. All pre-TimeCritical
            // shapes are ours to refresh.
            if (p.ThreadRules == null || p.ThreadRules.Count is not (2 or 3)) return false;

            bool IsPlainHighest(TunerThreadRule r, string desc) =>
                r.Description == desc && r.Priority == (int)ThreadPriorityLevel.Highest
                && r.StartAddress == string.Empty && !r.MatchAllThreads
                && r.BoostEnabled == null
                && r.AffinityMask == null && r.IdealGroup == null && r.MemoryPriority == null;
            bool IsPlainAboveNormal(TunerThreadRule r, string desc) =>
                r.Description == desc && r.Priority == (int)ThreadPriorityLevel.AboveNormal
                && r.StartAddress == string.Empty && !r.MatchAllThreads
                && r.BoostEnabled == null
                && r.AffinityMask == null && r.IdealGroup == null && r.MemoryPriority == null;

            if (p.Pattern == "*")
            {
                if (p.ThreadRules.Count == 2)
                {
                    return IsPlainHighest(p.ThreadRules[0], "Input")
                        && IsPlainAboveNormal(p.ThreadRules[1], "Sensor");
                }
                return IsPlainHighest(p.ThreadRules[0], "Input")
                    && IsPlainHighest(p.ThreadRules[1], "Sensor")
                    && IsPlainHighest(p.ThreadRules[2], "Kernel");
            }
            if (p.Pattern == "dwm.exe")
            {
                return IsPlainHighest(p.ThreadRules[0], "Master Input")
                    && IsPlainHighest(p.ThreadRules[1], "Kernel Sensor");
            }
            return false;
        }
        return false;
    }

    /// <summary>Shipped shapes of the retired "DWM & Master Input" rule.
    /// v1: High class, no thread rules. v2: High class + MatchAllThreads at
    /// Highest. User-customized variants are never touched.</summary>
    private static bool IsOldDwmMasterInputShipment(TunerProfile p)
    {
        if (p.Pattern != "dwm.exe, csrss.exe"
            || p.PriorityClass != (uint?)ProcessPriorityClass.High
            || p.GamingModeAuto)
        {
            return false;
        }
        if (p.ThreadRules == null || p.ThreadRules.Count == 0)
        {
            return true; // v1
        }
        if (p.ThreadRules.Count == 1)
        {
            var r = p.ThreadRules[0];
            return r.MatchAllThreads
                && r.Priority == (int)ThreadPriorityLevel.Highest
                && string.IsNullOrWhiteSpace(r.Description) && r.StartAddress == string.Empty
                && r.EfficiencyMode == null && r.BoostEnabled == null
                && r.AffinityMask == null && r.IdealGroup == null && r.MemoryPriority == null;
        }
        return false;
    }

    /// <summary>
    /// Serializes profile saves. Concurrent saves previously raced on a shared
    /// .tmp filename — one save moved the file out from under the other,
    /// logging "save FAILED: tmp not found" and, in the worst interleaving,
    /// letting a stale/empty snapshot win. Evidence: rules-debug.log.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public async Task<bool> SaveProfilesAsync()
    {
        try
        {
            List<TunerProfile> snapshot;
            lock (_lock)
            {
                snapshot = ActiveProfiles.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var text = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

            await _saveLock.WaitAsync();
            try
            {
                // Atomic write: a crash mid-save must never leave a truncated
                // file. Unique tmp name per save — a shared name raced between
                // concurrent saves (one Move stole the other's tmp file).
                string tmp = _profilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tmp, text);
                File.Move(tmp, _profilePath, overwrite: true);
            }
            finally
            {
                _saveLock.Release();
            }

            Log($"save ok: {snapshot.Count} profiles, {text.Length} bytes");
            return true;
        }
        catch (Exception ex)
        {
            Log("save FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    public async Task<bool> AddOrUpdate(TunerProfile profile)
    {
        lock (_lock)
        {
            if (!ActiveProfiles.Contains(profile))
            {
                ActiveProfiles.Add(profile);
            }
        }
        Log($"add: pattern=[{profile.Pattern}] actions={profile.ProcessActionCount} threadrules={profile.ThreadRules?.Count ?? 0}");
        bool saved = await SaveProfilesAsync();
        RaiseChanged();

        // The rule set may now include a gaming-mode rule whose process is
        // ALREADY running (rule created/edited mid-game). Re-evaluate so it
        // arms without needing an app restart or a game relaunch.
        EvaluateGamingModeRules();
        return saved;
    }

    public async Task<bool> Remove(TunerProfile profile)
    {
        lock (_lock)
        {
            ActiveProfiles.Remove(profile);
        }
        Log($"remove: pattern=[{profile.Pattern}]");
        bool saved = await SaveProfilesAsync();
        RaiseChanged();
        EvaluateGamingModeRules();
        return saved;
    }

    public List<TunerProfile> FindMatches(string processName)
    {
        lock (_lock)
        {
            return ActiveProfiles
                .Where(p => p.Enabled && p.AutoApply && ProfileMatcher.Matches(p.Pattern, processName))
                .ToList();
        }
    }

    /// <summary>Applies one profile to one pid. Never mutates the profile; returns the outcome.</summary>
    public async Task<RuleApplyResult> ApplyToProcessAsync(TunerProfile profile, int pid)
    {
        int attempted = 0;
        int succeeded = 0;
        var targets = new List<int>();

        if (profile.PriorityClass.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetPriorityAsync(pid, profile.PriorityClass.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.BoostEnabled.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetBoostAsync(pid, profile.BoostEnabled.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.EfficiencyMode.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetEfficiencyAsync(pid, profile.EfficiencyMode.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.AffinityMask.HasValue)
        {
            attempted++;
            try
            {
                await _tuning.SetAffinityAsync(pid, profile.AffinityMask.Value);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.CpuSetIds != null && profile.CpuSetIds.Count > 0)
        {
            attempted++;
            try
            {
                await _cpuSets.SetProcessCpuSetsAsync(pid, profile.CpuSetIds);
                succeeded++;
            }
            catch
            {
            }
        }

        if (profile.ThreadRules != null && profile.ThreadRules.Count > 0)
        {
            var live = await ThreadQueryService.ListThreadsAsync(pid);
            foreach (var rule in profile.ThreadRules)
            {
                var matched = live.Where(t => ThreadMatches(rule, t)).ToList();
                targets.Add(matched.Count);
                foreach (var t in matched)
                {
                    attempted++;
                    try
                    {
                        await _threads.SetPriorityAsync((uint)t.Tid, rule.Priority);
                        succeeded++;
                    }
                    catch
                    {
                    }

                    if (rule.EfficiencyMode.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetEfficiencyAsync((uint)t.Tid, rule.EfficiencyMode.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }

                    if (rule.BoostEnabled.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetBoostAsync((uint)t.Tid, rule.BoostEnabled.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }

                    if (rule.AffinityMask.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetAffinityAsync((uint)t.Tid, rule.AffinityGroup, rule.AffinityMask.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }

                    if (rule.IdealGroup.HasValue && rule.IdealIndex.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetIdealProcessorAsync((uint)t.Tid, rule.IdealGroup.Value, rule.IdealIndex.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }

                    if (rule.MemoryPriority.HasValue)
                    {
                        attempted++;
                        try
                        {
                            await _threads.SetMemoryPriorityAsync((uint)t.Tid, rule.MemoryPriority.Value);
                            succeeded++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        return new RuleApplyResult
        {
            ProcessFound = true,
            Attempted = attempted,
            Succeeded = succeeded,
            ThreadTargets = targets,
        };
    }

    /// <summary>
    /// Applies a profile to every currently-running matching process, updates
    /// LastResult.
    ///
    /// <paramref name="quiet"/> is the keeper/startup-sweep mode: the result line
    /// is written without the timestamp so an unchanged pass leaves the text
    /// identical, and the profile is only saved + broadcast when the line (or a
    /// thread target count) actually changed. Without this the 20 s sweep
    /// repainted the rules list and rewrote the rules file forever.
    /// </summary>
    public async Task<RuleApplyResult> ApplyProfileNowAsync(TunerProfile profile, bool quiet = false)
    {
        var pids = FindPids(profile.Pattern);
        Log($"apply-now: pattern=[{profile.Pattern}] pids=[{string.Join(",", pids)}]{(quiet ? " (keeper)" : "")}");
        if (pids.Count == 0)
        {
            // A pattern that equals the rule name can never match: say so
            // instead of leaving the rule looking like it is merely waiting.
            string waiting = PatternIsDisplayName(profile)
                ? "Pattern is the rule name — nothing can match it"
                : "Waiting for process";
            bool waitingChanged = profile.LastResult != waiting;
            profile.LastResult = waiting;
            profile.RefreshSummaries();
            if (waitingChanged)
            {
                await SaveProfilesAsync();
                RaiseChanged();
            }
            return new RuleApplyResult { ProcessFound = false };
        }

        int attempted = 0;
        int succeeded = 0;
        List<int> targets = new();
        foreach (int pid in pids)
        {
            var r = await ApplyToProcessAsync(profile, pid);
            attempted += r.Attempted;
            succeeded += r.Succeeded;
            if (targets.Count == 0)
            {
                targets = r.ThreadTargets;
            }
        }

        string previousResult = profile.LastResult;
        var previousTargets = profile.ThreadRules?.Select(r => r.TargetCount).ToList();
        CommitResult(profile, attempted, succeeded, targets, quiet);

        bool resultChanged = profile.LastResult != previousResult
            || (previousTargets is not null && profile.ThreadRules is not null
                && !previousTargets.SequenceEqual(profile.ThreadRules.Select(r => r.TargetCount)));

        if (!resultChanged)
        {
            // Nothing visible changed: stay silent so a background sweep cannot
            // repaint the UI or rewrite the rules file.
            return new RuleApplyResult
            {
                ProcessFound = true,
                Attempted = attempted,
                Succeeded = succeeded,
                ThreadTargets = targets,
            };
        }

        bool tracked;
        lock (_lock)
        {
            tracked = ActiveProfiles.Contains(profile);
        }
        if (tracked)
        {
            await SaveProfilesAsync();
        }
        else
        {
            // A detached profile must never trigger a save: persisting here
            // would write an empty/stale list over real rules. This exact
            // clobber once wiped a live rules file via a test harness.
            Log($"apply-now: pattern=[{profile.Pattern}] not in store, save skipped");
        }
        RaiseChanged();
        return new RuleApplyResult
        {
            ProcessFound = true,
            Attempted = attempted,
            Succeeded = succeeded,
            ThreadTargets = targets,
        };
    }

    public static bool ThreadMatches(TunerThreadRule rule, LiveThreadInfo thread)
    {
        if (rule.MatchAllThreads) return true;
        // Description is a contains-match: real thread names are descriptive
        // sentences ("HID input thread"), so exact equality never hit anything.
        // StartAddress stays exact ("module+offset" is already precise).
        bool descOk = string.IsNullOrWhiteSpace(rule.Description)
            || (thread.Description ?? string.Empty).Contains(rule.Description, StringComparison.OrdinalIgnoreCase);
        bool startOk = string.IsNullOrWhiteSpace(rule.StartAddress)
            || string.Equals(thread.StartAddress, rule.StartAddress, StringComparison.OrdinalIgnoreCase);
        return descOk && startOk && (!string.IsNullOrWhiteSpace(rule.Description) || !string.IsNullOrWhiteSpace(rule.StartAddress));
    }

    public static void CommitResult(TunerProfile profile, int attempted, int succeeded, List<int> targets, bool quiet = false)
    {
        profile.LastResult = attempted == 0
            ? "No actions defined"
            : quiet
                ? $"{succeeded}/{attempted} actions applied"
                : $"{succeeded}/{attempted} actions applied · {DateTime.Now:HH:mm:ss}";
        if (profile.ThreadRules != null)
        {
            for (int i = 0; i < profile.ThreadRules.Count && i < targets.Count; i++)
            {
                profile.ThreadRules[i].TargetCount = targets[i];
            }
        }
        profile.RefreshSummaries();
    }

    public static List<int> FindPids(string pattern)
    {
        var pids = new List<int>();
        if (string.IsNullOrWhiteSpace(pattern)) return pids;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (ProfileMatcher.Matches(pattern, proc.ProcessName))
                    {
                        pids.Add(proc.Id);
                    }
                }
                catch
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
        }
        return pids;
    }

    public void StartWatcher()
    {
        StopWatcher();
        try
        {
            _watcher = new ManagementEventWatcher(new EventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += OnProcessStarted;
            _watcher.Start();
        }
        catch
        {
            _watcher = null;
        }
    }

    public void StopWatcher()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.Stop();
                _watcher.EventArrived -= OnProcessStarted;
                _watcher.Dispose();
            }
            catch
            {
            }
            _watcher = null;
        }
    }

    private async void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var name = e.NewEvent.Properties["ProcessName"]?.Value as string;
            var pidProp = e.NewEvent.Properties["ProcessID"]?.Value;
            if (string.IsNullOrEmpty(name) || pidProp == null) return;

            int pid = Convert.ToInt32(pidProp);

            // Wait an extremely brief moment to allow the process core initialization,
            // while making the tuning application feel absolutely instantaneous.
            await Task.Delay(100);

            var matched = FindMatches(name);

            // Automatic Gaming mode: any enabled rule with GamingModeAuto
            // matching this launch arms app-wide Gaming mode. The exit watcher
            // deactivates it when the last armed process is gone.
            bool gamingRule = matched.Any(p => p.GamingModeAuto);

            if (gamingRule)
            {
                // Prune armed pids whose processes are already gone (a missed
                // stop event must not keep Gaming mode on forever).
                List<int> armed;
                lock (_gamingLock)
                {
                    _gamingModePids.RemoveWhere(p =>
                    {
                        try { Process.GetProcessById(p).Dispose(); return false; }
                        catch { return true; }
                    });
                    _gamingModePids.Add(pid);
                    armed = _gamingModePids.ToList();
                }

                StartGamingModeExitWatcher();
                StartGamingModeLivenessMonitor();
                // Take the RULE's hold. The session is refcounted, so this joins
                // an existing session instead of re-running the sweep, and the
                // hold is what stops this watcher from ever tearing down a
                // session the user or the benchmark is holding.
                await App.Current.GamingMode.AcquireAsync(
                    GameModeOwner.Rule, pid, armed, $"automatic gaming mode rule matched {name}");
                Log($"gaming-mode: rule hold taken for [{name}] pid={pid}, protected={armed.Count}");
            }

            bool benchmarkRule = matched.Any(p => p.AutoBenchmarkOnLaunch);
            if (benchmarkRule)
            {
                _ = App.Current.Benchmark.AutoStartCaptureAsync(pid, name, $"{name} auto-benchmark");
                Log($"auto-benchmark: triggered capture for [{name}] pid={pid}");
            }


            if (matched.Count == 0)
            {
                // Even with no rule match, persisted boost preferences must
                // still re-arm on every launch of the owning process (they are
                // independent of rules) — per thread and per process.
                _ = BoostPreferenceService.Instance.ApplyToProcessAsync(pid, name);
                _ = ProcessBoostPreferenceService.Instance.ApplyToProcessAsync(pid, name);
                return;
            }

            _ = BoostPreferenceService.Instance.ApplyToProcessAsync(pid, name);
            _ = ProcessBoostPreferenceService.Instance.ApplyToProcessAsync(pid, name);

            foreach (var profile in matched)
            {
                var result = await ApplyToProcessAsync(profile, pid);
                if (_dispatcher != null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        CommitResult(profile, result.Attempted, result.Succeeded, result.ThreadTargets);
                        _ = SaveProfilesAsync();
                        RaiseChanged();
                    });
                }
            }
        }
        catch
        {
            // Avoid failing WMI background pump due to process protection access denies
        }
    }

    /// <summary>Best-effort liveness check for the session's target.</summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Initial sweep at startup: applies every enabled auto-apply rule to
    /// processes that are ALREADY running (dwm.exe, csrss.exe, apps started
    /// before the tuner launched), then arms Gaming mode. Previously rules
    /// only triggered on WMI process-start events, so always-running system
    /// processes sat at "Waiting for process" forever.
    /// </summary>
    public void StartGamingModeWatcher()
    {
        _ = ApplyAllRulesToRunningProcessesAsync();
        EvaluateGamingModeRules();
    }

    /// <summary>Applies every enabled auto-apply profile to all currently
    /// running matching processes. Fire-and-forget startup sweep.</summary>
    public async Task ApplyAllRulesToRunningProcessesAsync()
    {
        List<TunerProfile> profiles;
        lock (_lock)
        {
            profiles = ActiveProfiles.Where(p => p.Enabled && p.AutoApply).ToList();
        }

        foreach (var profile in profiles)
        {
            try
            {
                await ApplyProfileNowAsync(profile, quiet: true);
            }
            catch (Exception ex)
            {
                Log($"startup-sweep: [{profile.Name}] failed: {ex.Message}");
            }
        }
    }

    private System.Threading.Timer? _keeperTimer;
    private int _keeperRunning;

    /// <summary>
    /// Cadence of the keep-applied sweep. Short enough that a setting Windows
    /// or the application itself moved back is restored before it can be
    /// missed, long enough to stay invisible in CPU time.
    /// </summary>
    public static readonly TimeSpan DefaultKeeperInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Starts the keep-applied sweep. Every <paramref name="interval"/> the
    /// enabled auto-apply rules are re-applied to the processes that are
    /// running, and the persisted priority-boost preferences (per thread and per
    /// process) are re-armed. A one-shot apply at process start always looked
    /// like "the settings keep resetting themselves" — a process restart, a
    /// thread created later, or Windows re-enabling boost undid it. This sweep
    /// is what makes Process Control settings permanent.
    /// </summary>
    public void StartKeeper(TimeSpan? interval = null)
    {
        var period = interval ?? DefaultKeeperInterval;
        if (period <= TimeSpan.Zero) return;

        _keeperTimer ??= new System.Threading.Timer(_ => _ = KeepAppliedAsync(), null, period, period);
        _keeperTimer.Change(period, period);
        Log($"keeper: sweeping every {period.TotalSeconds:0} s");
    }

    public void StopKeeper()
    {
        _keeperTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// One keeper pass: rules first, then the boost preferences. Re-entrancy
    /// guarded so a slow pass can never stack on top of the previous one.
    /// </summary>
    public async Task KeepAppliedAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _keeperRunning, 1) == 1) return;
        try
        {
            await ApplyAllRulesToRunningProcessesAsync();
            await BoostPreferenceService.Instance.ApplyToRunningProcessesAsync();
            await ProcessBoostPreferenceService.Instance.ApplyToRunningProcessesAsync();
        }
        catch (Exception ex)
        {
            Log("keeper FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _keeperRunning, 0);
        }
    }

    /// <summary>
    /// Re-evaluates gaming-mode rules against currently running processes:
    /// arms newly matching pids (rule created/edited mid-game, or app started
    /// mid-game), disarms pids no longer covered by any gaming rule, then
    /// activates or restores Gaming mode to match. Safe to call any time.
    /// </summary>
    public void EvaluateGamingModeRules()
    {
        try
        {
            List<TunerProfile> gamingRules;
            lock (_lock)
            {
                gamingRules = ActiveProfiles.Where(p => p.Enabled && p.GamingModeAuto).ToList();
            }

            var expected = new HashSet<int>();
            foreach (var profile in gamingRules)
            {
                foreach (int pid in FindPids(profile.Pattern))
                {
                    expected.Add(pid);
                }
            }

            lock (_gamingLock)
            {
                // Dead pids first (missed stop events)...
                _gamingModePids.RemoveWhere(p =>
                {
                    try { Process.GetProcessById(p).Dispose(); return false; }
                    catch { return true; }
                });
                // ...then pids whose rule disappeared or was edited to not match.
                _gamingModePids.RemoveWhere(p => !expected.Contains(p));
                _gamingModePids.UnionWith(expected);
            }

            List<int> armed;
            lock (_gamingLock)
            {
                armed = _gamingModePids.ToList();
            }

            if (armed.Count > 0)
            {
                StartGamingModeExitWatcher();
                StartGamingModeLivenessMonitor();
                // Idempotent by design: the hold registry joins the running
                // session when the target still matches, and hands the session
                // over when the process it belonged to has exited. No local
                // "did the watcher start it" flag is needed any more — the hold
                // IS that flag, and releasing it cannot touch other holders.
                _ = App.Current.GamingMode.AcquireAsync(
                        GameModeOwner.Rule, armed[0], armed, "automatic gaming mode rule")
                    .ContinueWith(_ => Log($"gaming-mode: evaluated, armed pids=[{string.Join(",", armed)}]"));
            }
            else if (App.Current.GamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: no armed processes remain (rules changed), releasing the rule's hold");
                App.Current.GamingMode.Release(GameModeOwner.Rule);
            }
        }
        catch (Exception ex)
        {
            Log("gaming-mode evaluate FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private ManagementEventWatcher? _exitWatcher;

    /// <summary>
    /// WMI stop events are best effort. This independent timer checks the
    /// armed game PIDs every second, so closing a game always restores even
    /// when WMI drops or delays Win32_ProcessStopTrace.
    /// </summary>
    private void StartGamingModeLivenessMonitor()
    {
        _gamingLivenessTimer ??= new System.Threading.Timer(
            _ => CheckGamingProcessLiveness(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        // A previous game may have stopped the existing timer; restart it for
        // the next automatic Gaming mode session.
        _gamingLivenessTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void CheckGamingProcessLiveness()
    {
        try
        {
            bool hasArmed;
            bool anyAlive = false;
            lock (_gamingLock)
            {
                hasArmed = _gamingModePids.Count > 0;
                _gamingModePids.RemoveWhere(p =>
                {
                    try
                    {
                        using var process = Process.GetProcessById(p);
                        anyAlive = true;
                        return false;
                    }
                    catch
                    {
                        return true;
                    }
                });

                if (_gamingModePids.Count > 0)
                {
                    anyAlive = true;
                }
            }

            if (hasArmed && !anyAlive && App.Current.GamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: liveness monitor found no armed game processes, releasing the rule's hold");
                App.Current.GamingMode.Release(GameModeOwner.Rule);
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            else if (anyAlive && App.Current.GamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                // Another armed game is still running, but the session may belong
                // to the one that just exited (two titles armed, the first closed).
                // Re-evaluating lets the registry hand the session to the survivor
                // instead of leaving it pointed at a dead PID.
                int? target = App.Current.GamingMode.SessionTargetPid;
                if (target.HasValue && !IsPidAlive(target.Value))
                {
                    Log("gaming-mode: session target exited while other armed games run — handing over");
                    EvaluateGamingModeRules();
                }
            }
        }
        catch
        {
            // The monitor must never terminate because one process disappeared.
        }
    }

    private void StartGamingModeExitWatcher()
    {
        if (_exitWatcher != null)
        {
            return;
        }

        try
        {
            _exitWatcher = new ManagementEventWatcher(new EventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _exitWatcher.EventArrived += OnProcessStopped;
            _exitWatcher.Start();
        }
        catch
        {
            _exitWatcher = null;
        }
    }

    public void StopGamingModeExitWatcher()
    {
        if (_exitWatcher != null)
        {
            try
            {
                _exitWatcher.Stop();
                _exitWatcher.EventArrived -= OnProcessStopped;
                _exitWatcher.Dispose();
            }
            catch
            {
            }
            _exitWatcher = null;
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pidProp = e.NewEvent.Properties["ProcessID"]?.Value;
            if (pidProp == null) return;

            int pid = Convert.ToInt32(pidProp);
            bool wasLast;
            lock (_gamingLock)
            {
                if (!_gamingModePids.Remove(pid))
                {
                    return; // not a gaming-mode process
                }

                wasLast = _gamingModePids.Count == 0;
            }

            if (wasLast && App.Current.GamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: last armed process exited, releasing the rule's hold");
                App.Current.GamingMode.Release(GameModeOwner.Rule);
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            else if (wasLast)
            {
                Log("gaming-mode: armed processes gone; manual Gaming mode session left untouched");
            }
        }
        catch
        {
            // Never let the WMI exit pump die.
        }
    }

    private void RaiseChanged()
    {
        if (_dispatcher != null)
        {
            _dispatcher.TryEnqueue(() => RulesChanged?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        StopKeeper();
        _keeperTimer?.Dispose();
        _keeperTimer = null;
        StopWatcher();
        StopGamingModeExitWatcher();
        _gamingLivenessTimer?.Dispose();
        _gamingLivenessTimer = null;
    }
}
