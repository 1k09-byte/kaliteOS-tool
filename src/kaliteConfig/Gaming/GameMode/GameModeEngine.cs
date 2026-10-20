using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using kaliteConfig.Native;

namespace kaliteConfig.Gaming.GameMode;

public sealed class GameModeEngine : IDisposable
{
    private readonly GameModeSettingsStore _store;
    private readonly GameModeGuard _guard = new();
    private readonly GameModeJournal _journal;
    private readonly GameModeExecutor _executor;
    private readonly GameModeLog _log;
    private readonly ForegroundWatcher _watcher = new();
    private readonly object _gate = new();

    private GameModeState _state = GameModeState.Idle;
    private string _activeGame = string.Empty;
    private int _activeGamePid;
    private long _activeGameTicks;
    private int _generation;
    private Timer? _debounceTimer;
    private Timer? _rescanTimer;
    private readonly List<GameModeAffectedRow> _rows = new();
    private bool _started;
    private bool _watchdogStarted;

    public GameModeEngine(GameModeSettingsStore store, GameModeLog log)
    {
        _store = store;
        _log = log;
        Settings = store.Load();
        _journal = new GameModeJournal(GameModeSettingsStore.JournalPath);
        _executor = new GameModeExecutor(_journal, log);
        _watcher.ForegroundChanged += OnForegroundChanged;
    }

    public event Action? Changed;

    public GameModeSettings Settings { get; }

    public GameModeState State { get { lock (_gate) return _state; } }

    public string ActiveGame { get { lock (_gate) return _activeGame; } }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                return _state switch
                {
                    GameModeState.Pending => $"Waiting for focus to settle for {_activeGame}",
                    GameModeState.Active => $"Active for {_activeGame}. {_rows.Count} process(es) affected.",
                    GameModeState.Releasing => "Releasing changes",
                    _ => Settings.Enabled ? "Idle. Start a listed game to activate." : "Off",
                };
            }
        }
    }

    public IReadOnlyList<GameModeAffectedRow> Affected { get { lock (_gate) return _rows.ToArray(); } }

    public IReadOnlyList<GameModeLogRow> LogRows => _log.Snapshot();

    public IReadOnlyList<GameModeGameEntry> Games => Settings.Games;

    public IReadOnlyList<string> NeverTouch => Settings.NeverTouch;

    public int PendingJournalCount => _journal.Count;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _watcher.Start();
        Recover();
        _log.Add("-", "Game Mode", "Start", "Applied and verified", "Foreground monitoring started");
        if (Settings.Enabled) Raise();
    }

    public void Recover()
    {
        if (_journal.Count == 0) return;
        int restored = _executor.RestoreAll("Recovered after unclean exit");
        _log.Add("-", "Game Mode", "Recovery", restored > 0 ? "Restored and verified" : "Could not restore",
            $"Recovered after unclean exit: {restored} process(es) restored");
    }

    public void Dispose()
    {
        try { _watcher.Dispose(); } catch { }
        _debounceTimer?.Dispose();
        _rescanTimer?.Dispose();
        ReleaseNow("kaliteConfig is closing");
    }

    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        _store.Save(Settings);
        if (enabled)
        {
            _log.Add("-", "Game Mode", "Enable", "Applied and verified", "Game Mode turned on");
        }
        else
        {
            ReleaseNow("Game Mode turned off");
            _log.Add("-", "Game Mode", "Disable", "Restored and verified", "Game Mode turned off");
        }
        Raise();
    }

    public void SetDebounce(int milliseconds)
    {
        Settings.DebounceMs = Math.Clamp(milliseconds, 250, 5000);
        _store.Save(Settings);
        Raise();
    }

    public void SetPreset(GameModePreset preset)
    {
        Settings.Preset = preset;
        _store.Save(Settings);
        Raise();
    }

    public void AddGame(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = ProcessPath(pid) ?? string.Empty;
            var entry = new GameModeGameEntry
            {
                Name = process.ProcessName + ".exe",
                Path = path,
                MatchByPath = !string.IsNullOrEmpty(path),
                Preset = Settings.Preset,
            };
            if (Settings.Games.Any(g => string.Equals(g.Path, entry.Path, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(g.Name, entry.Name, StringComparison.OrdinalIgnoreCase))) return;
            Settings.Games.Add(entry);
            _store.Save(Settings);
            _log.Add(entry.Name, "Game list", "Add", "Applied and verified", "Added to the game list");
            Raise();
        }
        catch (Exception ex)
        {
            _log.Add(pid.ToString(), "Game list", "Add", "Refused", ex.Message);
        }
    }

    public void AddGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var entry = new GameModeGameEntry
        {
            Name = System.IO.Path.GetFileName(path),
            Path = path,
            MatchByPath = true,
            Preset = Settings.Preset,
        };
        if (Settings.Games.Any(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        Settings.Games.Add(entry);
        _store.Save(Settings);
        _log.Add(entry.Name, "Game list", "Add", "Applied and verified", "Added to the game list by path");
        Raise();
    }

    public void RemoveGame(string name)
    {
        var removed = Settings.Games.RemoveAll(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(g.Path, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        _store.Save(Settings);
        Raise();
    }

    public void ClearLog()
    {
        _log.Clear();
        Raise();
    }

    public void AddNeverTouch(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        if (Settings.NeverTouch.Any(n => n.Equals(pattern, StringComparison.OrdinalIgnoreCase))) return;
        Settings.NeverTouch.Add(pattern.Trim());
        _store.Save(Settings);
        Raise();
    }

    public void RemoveNeverTouch(string pattern)
    {
        if (Settings.NeverTouch.RemoveAll(n => n.Equals(pattern, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _store.Save(Settings);
        Raise();
    }

    public int ResumeEverything()
    {
        int restored = _executor.RestoreAll("Resume everything now");
        lock (_gate)
        {
            _rows.Clear();
            _state = GameModeState.Idle;
            _activeGame = string.Empty;
            _generation++;
        }
        _log.Add("-", "Game Mode", "Resume everything", "Restored and verified", $"{restored} process(es) restored");
        Raise();
        return restored;
    }

    public IReadOnlyList<GameModeAffectedRow> Preview()
    {
        string gameName;
        string? gamePath;
        lock (_gate)
        {
            if (_state == GameModeState.Active && !string.IsNullOrEmpty(_activeGame))
            {
                gameName = _activeGame;
                gamePath = _activeGamePid > 0 ? ProcessPath(_activeGamePid) : null;
            }
            else if (Settings.Games.Count > 0)
            {
                gameName = Settings.Games[0].Name;
                gamePath = Settings.Games[0].MatchByPath ? Settings.Games[0].Path : null;
            }
            else
            {
                return Array.Empty<GameModeAffectedRow>();
            }
        }

        var facts = BuildFacts();
        var gamePids = new HashSet<int>();
        var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(gamePath)) gamePaths.Add(gamePath);

        var rows = new List<GameModeAffectedRow>();
        foreach (var fact in facts)
        {
            var entry = BackgroundCatalog.FindByImage(fact.Name);
            if (entry == null) continue;
            var (action, category, outcome, message) = Plan(fact, entry, gamePids, gamePaths);
            rows.Add(new GameModeAffectedRow(fact.Name, fact.Pid, Label(category), Label(action), OutcomeLabel(outcome), message));
        }
        _log.Add(gameName, "Preview", "Preview", "Applied and verified", $"Previewed {rows.Count} background process(es) without changing anything");
        Raise();
        return rows;
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (!Settings.Enabled) return;
        if (!ForegroundWatcher.TryResolveForeground(hwnd, out int pid, out string name, out string? imagePath)) return;

        var match = FindGame(pid, name, imagePath);
        if (match != null)
        {
            bool alreadyActive;
            lock (_gate)
            {
                alreadyActive = _state == GameModeState.Active && _activeGamePid == pid;
                if (alreadyActive) return;
                _activeGame = match.Name;
                _activeGamePid = pid;
                _activeGameTicks = TryStartTicks(pid);
                _state = GameModeState.Pending;
                int generation = ++_generation;
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ => Activate(generation, pid, match.Name), null, Settings.ClampedDebounce, Timeout.Infinite);
            }
            _log.Add(match.Name, "Focus", "Pending", "Applied and verified", $"Game focused; activating in {Settings.ClampedDebounce} ms");
            Raise();
            return;
        }

        lock (_gate)
        {
            if (_state == GameModeState.Idle) return;
        }
        BeginRelease($"focus moved to {name}");
    }

    private void Activate(int generation, int gamePid, string gameName)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            if (!GameModeExecutor.SameInstance(gamePid, _activeGameTicks))
            {
                _state = GameModeState.Idle;
                _activeGame = string.Empty;
            }
            else
            {
                _state = GameModeState.Active;
            }
        }

        if (State != GameModeState.Active)
        {
            Raise();
            return;
        }

        var facts = BuildFacts();
        var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = ProcessPath(gamePid);
        if (!string.IsNullOrEmpty(path)) gamePaths.Add(path);
        var gamePids = GameModeGuard.ExpandGameTree(facts, gamePid);

        int applied = ApplyPlan(facts, gamePids, gamePaths, gameName);
        EnsureWatchdog();

        lock (_gate)
        {
            _rescanTimer?.Dispose();
            _rescanTimer = new Timer(_ => Rescan(gamePid, gameName), null, 3000, 3000);
        }

        _log.Add(gameName, "Focus", "Active", "Applied and verified", $"Game mode active; {applied} process(es) affected");
        Raise();
    }

    private void Rescan(int gamePid, string gameName)
    {
        lock (_gate)
        {
            if (_state != GameModeState.Active || _activeGamePid != gamePid) return;
        }

        if (!GameModeExecutor.SameInstance(gamePid, _activeGameTicks))
        {
            BeginRelease($"{gameName} exited");
            return;
        }

        var facts = BuildFacts();
        var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = ProcessPath(gamePid);
        if (!string.IsNullOrEmpty(path)) gamePaths.Add(path);
        var gamePids = GameModeGuard.ExpandGameTree(facts, gamePid);
        int applied = ApplyPlan(facts, gamePids, gamePaths, gameName);
        if (applied > 0) Raise();
    }

    private int ApplyPlan(IReadOnlyList<ProcessFact> facts, IReadOnlySet<int> gamePids, IReadOnlySet<string> gamePaths, string gameName)
    {
        var journaled = _journal.Snapshot().Select(e => (e.Pid, e.CreationTicks)).ToHashSet();
        int applied = 0;
        foreach (var fact in facts)
        {
            if (applied >= 40) break;
            if (journaled.Contains((fact.Pid, fact.CreationTicks))) continue;
            var entry = BackgroundCatalog.FindByImage(fact.Name);
            if (entry == null) continue;

            var (action, category, outcome, message) = Plan(fact, entry, gamePids, gamePaths);
            if (outcome != GameModeOutcome.Applicable) continue;
            if (action == BackgroundAction.None) continue;

            var decision = _executor.Apply(fact, action, GameModeOutcome.Applicable, action, Settings);
            lock (_gate)
            {
                _rows.RemoveAll(r => r.Pid == fact.Pid);
                _rows.Add(new GameModeAffectedRow(fact.Name, fact.Pid, Label(category), Label(decision.Planned), OutcomeLabel(decision.Outcome), decision.Message));
            }
            _log.Add(fact.Name, Label(decision.Planned), OutcomeLabel(decision.Outcome), decision.Message);
            if (decision.Outcome == GameModeOutcome.AppliedVerified || decision.Outcome == GameModeOutcome.AppliedUnverified)
                applied++;
        }
        return applied;
    }

    private GameModeOutcomeLabelTuple Plan(ProcessFact fact, BackgroundCatalogEntry entry, IReadOnlySet<int> gamePids, IReadOnlySet<string> gamePaths)
    {
        var guard = _guard.Evaluate(fact, gamePids, gamePaths, Settings);
        if (!guard.Allowed)
            return new(BackgroundAction.None, entry.Category, GameModeOutcome.SkippedProtected, "Skipped (protected: " + guard.Reason + ")");

        if (entry.Category == BackgroundCategory.CaptureTools || entry.Category == BackgroundCategory.Overlays)
        {
            var throttle = entry.Category == BackgroundCategory.Overlays ? BackgroundAction.Efficiency : BackgroundAction.LowerPriority;
            return new(throttle, entry.Category, GameModeOutcome.Applicable, "Throttle only; never suspended");
        }

        var configured = Settings.Actions.TryGetValue(entry.Id, out var chosen) ? chosen : entry.DefaultAction;
        configured = Settings.Preset switch
        {
            GameModePreset.Light => configured == BackgroundAction.Suspend ? BackgroundAction.Efficiency : configured,
            GameModePreset.Aggressive => configured == BackgroundAction.None ? BackgroundAction.Efficiency : configured,
            _ => configured,
        };

        if (configured == BackgroundAction.Suspend)
        {
            if (!entry.SuspendEligible)
                return new(BackgroundAction.Efficiency, entry.Category, GameModeOutcome.Downgraded, "Overlay or capture tool; efficiency only");
            return new(BackgroundAction.Efficiency, entry.Category, GameModeOutcome.Downgraded, "Suspend is disabled on this build; efficiency mode instead");
        }

        return new(configured, entry.Category, GameModeOutcome.Applicable, "Planned");
    }

    private void BeginRelease(string reason)
    {
        lock (_gate)
        {
            if (_state == GameModeState.Releasing) return;
            _state = GameModeState.Releasing;
            _debounceTimer?.Dispose();
            _rescanTimer?.Dispose();
            _generation++;
        }
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            try
            {
                int restored = _executor.RestoreAll(reason);
                watch.Stop();
                lock (_gate)
                {
                    _rows.Clear();
                    _activeGame = string.Empty;
                    _activeGamePid = 0;
                    _state = GameModeState.Idle;
                }
                _log.Add("-", "Game Mode", "Release", "Restored and verified",
                    $"Released in {watch.ElapsedMilliseconds} ms ({restored} process(es)) after {reason}");
            }
            catch (Exception ex)
            {
                lock (_gate) _state = GameModeState.Idle;
                _log.Add("-", "Game Mode", "Release", "Could not restore", ex.Message);
            }
            Raise();
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "GameModeRelease",
        };
        thread.Start();
        Raise();
    }

    private void ReleaseNow(string reason)
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _rescanTimer?.Dispose();
            _generation++;
            _state = GameModeState.Idle;
            _activeGame = string.Empty;
            _activeGamePid = 0;
            _rows.Clear();
        }
        _executor.RestoreAll(reason);
        Raise();
    }

    private void EnsureWatchdog()
    {
        if (_watchdogStarted) return;
        _watchdogStarted = true;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--game-mode-guard", Environment.ProcessId.ToString(), GameModeSettingsStore.JournalPath },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            _log.Add("-", "Game Mode", "Watchdog", "Applied and verified", "Guard process started for crash safety");
        }
        catch (Exception ex)
        {
            _log.Add("-", "Game Mode", "Watchdog", "Skipped (safety check failed: " + ex.Message + ")", "Guard could not start");
        }
    }

    public static bool TryRunGuard(string[] args)
    {
        int flag = Array.FindIndex(args, a => a.Equals("--game-mode-guard", StringComparison.OrdinalIgnoreCase));
        if (flag < 0 || args.Length < flag + 3) return false;
        if (!int.TryParse(args[flag + 1], out int parentPid)) return false;
        string journalPath = args[flag + 2];
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch
        {
        }
        try
        {
            var journal = new GameModeJournal(journalPath);
            var log = new GameModeLog(GameModeSettingsStore.LogPath);
            var executor = new GameModeExecutor(journal, log);
            int restored = executor.RestoreAll("kaliteConfig exited unexpectedly");
            log.Add("-", "Watchdog", "Recovery", restored > 0 ? "Restored and verified" : "Already in that state",
                $"Watchdog restored {restored} process(es) after the app exited");
        }
        catch
        {
        }
        return true;
    }

    private GameModeGameEntry? FindGame(int pid, string name, string? imagePath)
    {
        foreach (var game in Settings.Games)
        {
            if (game.MatchByPath && !string.IsNullOrEmpty(game.Path) && !string.IsNullOrEmpty(imagePath)
                && string.Equals(game.Path, imagePath, StringComparison.OrdinalIgnoreCase))
                return game;
            if (string.Equals(game.Name, name, StringComparison.OrdinalIgnoreCase))
                return game;
        }
        return null;
    }

    private static long TryStartTicks(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.Ticks; }
        catch { return 0; }
    }

    private static string? ProcessPath(int pid)
    {
        try
        {
            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return NativeMethods.GameMode.QueryFullProcessImageName(handle.DangerousGetHandle(), 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<ProcessFact> BuildFacts()
    {
        var facts = new List<ProcessFact>();
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = NativeMethods.Toolhelp.CreateToolhelp32Snapshot(NativeMethods.Toolhelp.TH32CS_SNAPPROCESS, 0);
        if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new NativeMethods.Toolhelp.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.PROCESSENTRY32>() };
                if (NativeMethods.Toolhelp.Process32First(snapshot, ref entry))
                {
                    do
                    {
                        parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                    }
                    while (NativeMethods.Toolhelp.Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                NativeMethods.Handles.CloseHandle(snapshot);
            }
        }

        _guard.SnapshotServicePids();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid <= 4 || pid == Environment.ProcessId) continue;
                string name = process.ProcessName + ".exe";
                if (BackgroundCatalog.FindByImage(name) == null) continue;
                long ticks;
                int session;
                try
                {
                    ticks = process.StartTime.Ticks;
                    session = process.SessionId;
                }
                catch
                {
                    continue;
                }
                facts.Add(new ProcessFact(pid, parents.GetValueOrDefault(pid, 0), name, ProcessPath(pid), ticks, session));
            }
            finally
            {
                process.Dispose();
            }
        }
        return facts;
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public static string Label(BackgroundAction action) => action switch
    {
        BackgroundAction.None => "None",
        BackgroundAction.LowerPriority => "Lower priority",
        BackgroundAction.Efficiency => "Efficiency mode",
        BackgroundAction.ConfineCores => "Reserved cores",
        _ => "Suspend",
    };

    public static string Label(BackgroundCategory category) => category switch
    {
        BackgroundCategory.CloudSync => "Cloud sync",
        BackgroundCategory.UserAdded => "User added",
        BackgroundCategory.CaptureTools => "Capture tools",
        _ => category.ToString(),
    };

    public static string OutcomeLabel(GameModeOutcome outcome) => outcome switch
    {
        GameModeOutcome.Applicable => "Ready",
        GameModeOutcome.AppliedVerified => "Applied and verified",
        GameModeOutcome.AppliedUnverified => "Applied (couldn't verify)",
        GameModeOutcome.Refused => "Refused (access denied)",
        GameModeOutcome.SkippedProtected => "Skipped (protected)",
        GameModeOutcome.SkippedSafety => "Skipped (safety check)",
        GameModeOutcome.Downgraded => "Downgraded",
        GameModeOutcome.AlreadyInState => "Already in that state",
        GameModeOutcome.RestoredVerified => "Restored and verified",
        _ => "Could not restore",
    };
}

public readonly record struct GameModeOutcomeLabelTuple(
    BackgroundAction Action,
    BackgroundCategory Category,
    GameModeOutcome Outcome,
    string Message);
