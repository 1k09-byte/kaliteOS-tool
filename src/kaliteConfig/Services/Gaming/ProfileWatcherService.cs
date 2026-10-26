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

public sealed class RuleApplyResult
{
    public bool ProcessFound { get; init; }
    public int Attempted { get; init; }
    public int Succeeded { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public List<int> ThreadTargets { get; init; } = new();
}

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

    private readonly HashSet<int> _gamingModePids = new();
    private readonly object _gamingLock = new();
    private System.Threading.Timer? _gamingLivenessTimer;

    public List<TunerProfile> ActiveProfiles { get; private set; } = new();

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
        LoadRuleGameModeConsent();
        RaiseChanged();
    }

    public const string SelfProcessPattern = "kaliteConfig.exe";

    private static string GameModeConsentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "gamingmode-rules.json");

    public bool AllowRuleAutoGameMode { get; private set; } = true;

    private void LoadRuleGameModeConsent()
    {
        try
        {
            if (File.Exists(GameModeConsentPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(GameModeConsentPath));
                if (doc.RootElement.TryGetProperty("Allow", out var el) &&
                    (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
                {
                    AllowRuleAutoGameMode = el.GetBoolean();
                    return;
                }
            }
            lock (_lock)
            {
                AllowRuleAutoGameMode = ActiveProfiles.Any(p => p.Enabled && p.GamingModeAuto);
            }
            SaveRuleGameModeConsent();
        }
        catch { }
    }

    public void SetAllowRuleAutoGameMode(bool value)
    {
        AllowRuleAutoGameMode = value;
        SaveRuleGameModeConsent();
        Log($"rule game-mode consent: {(value ? "allowed" : "blocked")}");
        RaiseChanged();
    }

    private void SaveRuleGameModeConsent()
    {
        try { File.WriteAllText(GameModeConsentPath, "{\"Allow\":" + (AllowRuleAutoGameMode ? "true" : "false") + "}"); }
        catch { }
    }

    private List<TunerProfile> CreateBuiltInDefaults()
    {
        return new List<TunerProfile>();
    }

    public async Task EnsureBuiltInDefaultsAsync()
    {
        bool changed = false;
        var defaults = CreateBuiltInDefaults();
        lock (_lock)
        {
            for (int i = ActiveProfiles.Count - 1; i >= 0; i--)
            {
                if (ActiveProfiles[i].Name == "DWM & Master Input" && IsOldDwmMasterInputShipment(ActiveProfiles[i]))
                {
                    Log($"defaults: removed retired built-in 'DWM & Master Input'");
                    ActiveProfiles.RemoveAt(i);
                    changed = true;
                }
                else if (TouchesProtectedSystemProcess(ActiveProfiles[i].Pattern))
                {
                    Log($"defaults: removed profile that tuned a protected system process ([{ActiveProfiles[i].Name}])");
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
                else if (def.Name == "kaliteConfig (this app)" && IsOursToRefreshSelfRule(existing))
                {
                    Log($"defaults: refreshed built-in '{def.Name}' to Idle / no boost / Efficiency mode");
                    existing.Pattern = def.Pattern;
                    existing.PriorityClass = def.PriorityClass;
                    existing.BoostEnabled = def.BoostEnabled;
                    existing.EfficiencyMode = def.EfficiencyMode;
                    existing.AutoApply = def.AutoApply;
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

    internal static bool PatternIsDisplayName(TunerProfile p) =>
        RulePatternGuard.IsRuleNameUsedAsPattern(p.Pattern, p.Name);

    private static bool IsOursToRefreshSelfRule(TunerProfile p)
    {
        if (p.GamingModeAuto) return false;
        if (p.ProtectThreads) return false;
        if (p.ThreadRules != null && p.ThreadRules.Count > 0) return false;
        if (p.AffinityMask.HasValue) return false;
        if (p.CpuSetIds != null && p.CpuSetIds.Count > 0) return false;

        bool nothingSet = !p.PriorityClass.HasValue && !p.BoostEnabled.HasValue && !p.EfficiencyMode.HasValue;
        if (nothingSet) return true;

        return (p.PriorityClass.HasValue && p.PriorityClass.Value == (uint)TunerPriorityClass.Idle)
            && (p.BoostEnabled.HasValue && p.BoostEnabled.Value == false)
            && (p.EfficiencyMode.HasValue && p.EfficiencyMode.Value == true);
    }

    private static bool IsOldShippedContent(TunerProfile p)
    {
        if (p.Name == "DWM & Master Input")
        {
            return IsOldDwmMasterInputShipment(p);
        }
        return false;
    }

    private static readonly string[] ProtectedProcessPatterns =
    {
        "dwm", "csrss", "winlogon", "wininit", "services", "lsass", "smss", "system", "registry", "memory compression",
        "kaliteconfig",
    };

    internal static bool TouchesProtectedSystemProcess(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        foreach (string part in pattern.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = part.Trim('*', ' ').ToLowerInvariant();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (ProtectedProcessPatterns.Contains(name)) return true;
        }
        return false;
    }

    private static bool IsProtectedProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return ProtectedProcessPatterns.Contains(process.ProcessName.ToLowerInvariant());
        }
        catch { return true; }
    }

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
            return true;
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

    public async Task<RuleApplyResult> ApplyProfileNowAsync(TunerProfile profile, bool quiet = false)
    {
        var pids = FindPids(profile.Pattern);
        if (TouchesProtectedSystemProcess(profile.Pattern))
        {
            pids = pids.Where(pid => !IsProtectedProcess(pid)).ToList();
        }
        Log($"apply-now: pattern=[{profile.Pattern}] pids=[{string.Join(",", pids)}]{(quiet ? " (keeper)" : "")}");
        if (pids.Count == 0)
        {
            string waiting = PatternIsDisplayName(profile)
                ? "Pattern is the rule name - nothing can match it"
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

            await Task.Delay(100);

            var matched = FindMatches(name);

            bool gamingRule = matched.Any(p => p.GamingModeAuto);

            if (gamingRule && !AllowRuleAutoGameMode)
            {
                Log($"gaming-mode: blocked automatic hold for [{name}] (Rules can enable Game Mode automatically is off)");
            }
            else if (gamingRule)
            {
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
                await App.Current.LegacyGamingMode.AcquireAsync(
                    GameModeOwner.Rule, pid, armed, $"automatic gaming mode rule matched {name}");
                Log($"gaming-mode: rule hold taken for [{name}] pid={pid}, protected={armed.Count}");
            }

            if (matched.Count == 0)
            {
                _ = ReapplyBoostPreferencesAsync(pid, name);
                return;
            }

            _ = ReapplyBoostPreferencesAsync(pid, name);

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
        }
    }

    private static async Task ReapplyBoostPreferencesAsync(int pid, string name)
    {
        try
        {
            await BoostPreferenceRules.ApplyBoostLayersInOrderAsync(
                () => ProcessBoostPreferenceService.Instance.ApplyToProcessAsync(pid, name),
                () => BoostPreferenceService.Instance.ApplyToProcessAsync(pid, name));
        }
        catch
        {
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    public void StartGamingModeWatcher()
    {
        _ = ApplyAllRulesToRunningProcessesAsync();
        EvaluateGamingModeRules();
    }

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

    public static readonly TimeSpan DefaultKeeperInterval = TimeSpan.FromSeconds(20);

    public void StartKeeper(TimeSpan? interval = null)
    {
        var period = interval ?? DefaultKeeperInterval;
        if (period <= TimeSpan.Zero) return;

        _keeperTimer ??= new System.Threading.Timer(_ => _ = KeepAppliedAsync(), null, 1000, (int)period.TotalMilliseconds);
        _keeperTimer.Change(1000, (int)period.TotalMilliseconds);
        Log($"keeper: sweeping every {period.TotalSeconds:0} s");
    }

    public void StopKeeper()
    {
        _keeperTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public async Task KeepAppliedAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _keeperRunning, 1) == 1) return;
        try
        {
            await ApplyAllRulesToRunningProcessesAsync();
            await BoostPreferenceRules.ApplyBoostLayersInOrderAsync(
                static () => ProcessBoostPreferenceService.Instance.ApplyToRunningProcessesAsync(),
                static () => BoostPreferenceService.Instance.ApplyToRunningProcessesAsync());
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
                _gamingModePids.RemoveWhere(p =>
                {
                    try { Process.GetProcessById(p).Dispose(); return false; }
                    catch { return true; }
                });
                _gamingModePids.RemoveWhere(p => !expected.Contains(p));
                _gamingModePids.UnionWith(expected);
            }

            List<int> armed;
            lock (_gamingLock)
            {
                armed = _gamingModePids.ToList();
            }

            if (armed.Count > 0 && !AllowRuleAutoGameMode)
            {
                Log("gaming-mode: rule hold blocked (Rules can enable Game Mode automatically is off)");
            }
            else if (armed.Count > 0)
            {
                StartGamingModeExitWatcher();
                StartGamingModeLivenessMonitor();
                _ = App.Current.LegacyGamingMode.AcquireAsync(
                        GameModeOwner.Rule, armed[0], armed, "automatic gaming mode rule")
                    .ContinueWith(_ => Log($"gaming-mode: evaluated, armed pids=[{string.Join(",", armed)}]"));
            }
            else if (App.Current.LegacyGamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: no armed processes remain (rules changed), releasing the rule's hold");
                App.Current.LegacyGamingMode.Release(GameModeOwner.Rule);
            }
        }
        catch (Exception ex)
        {
            Log("gaming-mode evaluate FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private ManagementEventWatcher? _exitWatcher;

    private void StartGamingModeLivenessMonitor()
    {
        _gamingLivenessTimer ??= new System.Threading.Timer(
            _ => CheckGamingProcessLiveness(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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

            if (hasArmed && !anyAlive && App.Current.LegacyGamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: liveness monitor found no armed game processes, releasing the rule's hold");
                App.Current.LegacyGamingMode.Release(GameModeOwner.Rule);
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            else if (anyAlive && App.Current.LegacyGamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                int? target = App.Current.LegacyGamingMode.SessionTargetPid;
                if (target.HasValue && !IsPidAlive(target.Value))
                {
                    Log("gaming-mode: session target exited while other armed games run - handing over");
                    EvaluateGamingModeRules();
                }
            }
        }
        catch
        {
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
                    return;
                }

                wasLast = _gamingModePids.Count == 0;
            }

            if (wasLast && App.Current.LegacyGamingMode.IsHeldBy(GameModeOwner.Rule))
            {
                Log("gaming-mode: last armed process exited, releasing the rule's hold");
                App.Current.LegacyGamingMode.Release(GameModeOwner.Rule);
                _gamingLivenessTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            else if (wasLast)
            {
                Log("gaming-mode: armed processes gone; manual Gaming mode session left untouched");
            }
        }
        catch
        {
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
