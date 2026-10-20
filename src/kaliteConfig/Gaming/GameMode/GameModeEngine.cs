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
    private readonly Action<string> _watcherDiagnostic;
    private readonly object _gate = new();

    private GameModeState _state = GameModeState.Idle;
    private string _activeGame = string.Empty;
    private int _activeGamePid;
    private long _activeGameTicks;
    private int _generation;
    private Timer? _debounceTimer;
    private Timer? _rescanTimer;
    private readonly List<GameModeAffectedRow> _rows = new();
    private readonly HashSet<string> _loggedSkips = new();
    private int _lastForeignPid;
    private long _lastForeignTicks;
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
        _watcherDiagnostic = message => _log.Add("Game Mode", "Watcher", "Applied and verified", message);
        _watcher.Diagnostic += _watcherDiagnostic;
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
        if (_journal.LoadFailed)
        {
            _log.Add("-", "Game Mode", "Recovery", "Could not restore",
                "The journal could not be read, so earlier changes may not have been restored");
            return;
        }
        if (_journal.Count == 0) return;
        var summary = _executor.RestoreAll("Recovered after unclean exit");
        _log.Add("-", "Game Mode", "Recovery", summary.Failed > 0 ? "Could not restore" : "Restored and verified",
            summary.Failed > 0
                ? $"Recovered after unclean exit: {summary.Restored} restored, {summary.Failed} could not be restored"
                : $"Recovered after unclean exit: {summary.Restored} process(es) restored");
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
            var hwnd = NativeMethods.GameMode.GetForegroundWindow();
            if (hwnd != IntPtr.Zero) OnForegroundChanged(hwnd);
        }
        else
        {
            var summary = ReleaseNow("Game Mode turned off");
            _log.Add("-", "Game Mode", "Disable", summary.Failed > 0 ? "Could not restore" : "Restored and verified",
                summary.Failed > 0 ? $"Game Mode turned off; {summary.Failed} process(es) could not be restored" : "Game Mode turned off");
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

    public void AddFocusedApp()
    {
        var hwnd = NativeMethods.GameMode.GetForegroundWindow();
        if (ForegroundWatcher.TryResolveForeground(hwnd, out int pid, out _, out _) && pid != Environment.ProcessId)
        {
            AddGame(pid);
            return;
        }

        int last;
        long ticks;
        lock (_gate)
        {
            last = _lastForeignPid;
            ticks = _lastForeignTicks;
        }
        if (last != 0 && GameModeExecutor.SameInstance(last, ticks)) AddGame(last);
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

    public void RemoveGame(string name, string path)
    {
        var removed = Settings.Games.RemoveAll(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)
                                                    && string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase));
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
        var summary = _executor.RestoreAll("Resume everything now");
        lock (_gate)
        {
            _generation++;
            _state = GameModeState.Idle;
            _activeGame = string.Empty;
            _activeGamePid = 0;
            _activeGameTicks = 0;
            _loggedSkips.Clear();
            ApplyRestoreResultLocked();
        }
        _log.Add("-", "Game Mode", "Resume everything", summary.Failed > 0 ? "Could not restore" : "Restored and verified",
            summary.Failed > 0
                ? $"{summary.Restored} process(es) restored, {summary.Failed} could not be restored and stay in the journal"
                : $"{summary.Restored} process(es) restored");
        Raise();
        return summary.Restored;
    }

    public IReadOnlyList<GameModeAffectedRow> Preview()
    {
        string gameName;
        string? gamePath;
        int gamePid = 0;
        lock (_gate)
        {
            if (_state == GameModeState.Active && !string.IsNullOrEmpty(_activeGame) && _activeGamePid > 0)
            {
                gameName = _activeGame;
                gamePid = _activeGamePid;
                gamePath = ProcessPath(_activeGamePid);
            }
            else if (FindFocusedGame(out int focusedPid, out GameModeGameEntry? focused) && focused != null)
            {
                gameName = focused.Name;
                gamePid = focusedPid;
                gamePath = focused.MatchByPath ? focused.Path : ProcessPath(focusedPid);
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
        var gamePids = gamePid > 0 ? GameModeGuard.ExpandGameTree(facts, gamePid) : new HashSet<int>();
        var gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(gamePath)) gamePaths.Add(gamePath);

        var rows = new List<GameModeAffectedRow>();
        foreach (var fact in facts)
        {
            var entry = BackgroundCatalog.FindByImage(fact.Name);
            if (entry == null) continue;
            var (action, category, outcome, message) = Plan(fact, entry, gamePids, gamePaths);
            rows.Add(new GameModeAffectedRow(fact.Name, fact.Pid, Label(category), Label(action), OutcomeLabel(outcome), message, fact.CreationTicks));
        }
        _log.Add(gameName, "Preview", "Preview", "Previewed (nothing changed)", $"Previewed {rows.Count} background process(es) for {gameName} without changing anything");
        return rows;
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (!Settings.Enabled) return;
        if (!ForegroundWatcher.TryResolveForeground(hwnd, out int pid, out string name, out string? imagePath))
            return;

        if (pid != Environment.ProcessId)
        {
            lock (_gate)
            {
                _lastForeignPid = pid;
                _lastForeignTicks = TryStartTicks(pid);
            }
        }

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

        _log.Add("Game Mode", "Scan", "Applied and verified", $"{facts.Count} background candidates, {gamePids.Count} protected game-tree process(es)");
        lock (_gate) _loggedSkips.Clear();
        ApplyPlan(facts, gamePids, gamePaths, gameName);
        EnsureWatchdog();

        lock (_gate)
        {
            _rescanTimer?.Dispose();
            _rescanTimer = new Timer(_ => Rescan(gamePid, gameName), null, 3000, 3000);
        }

        int held = Affected.Count;
        _log.Add(gameName, "Focus", "Active", "Applied and verified", $"Game mode active; {held} process(es) affected");
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
        int before = Affected.Count;
        ApplyPlan(facts, gamePids, gamePaths, gameName);
        if (Affected.Count != before) Raise();
    }

    private int ApplyPlan(IReadOnlyList<ProcessFact> facts, IReadOnlySet<int> gamePids, IReadOnlySet<string> gamePaths, string gameName)
    {
        PruneJournal();
        PruneRows();
        var journaled = _journal.Snapshot().Select(e => (e.Pid, e.CreationTicks)).ToHashSet();
        int cap = Math.Max(0, 40 - journaled.Count);
        int applied = 0;
        foreach (var fact in facts)
        {
            if (applied >= cap) break;
            if (journaled.Contains((fact.Pid, fact.CreationTicks))) continue;
            var entry = BackgroundCatalog.FindByImage(fact.Name);
            if (entry == null) continue;

            var (action, category, outcome, message) = Plan(fact, entry, gamePids, gamePaths);
            if (outcome == GameModeOutcome.SkippedProtected)
            {
                bool first;
                lock (_gate) first = _loggedSkips.Add(fact.Pid + ":" + fact.CreationTicks);
                if (first) _log.Add(fact.Name, "Skip", OutcomeLabel(outcome), message);
                continue;
            }
            if (outcome != GameModeOutcome.Applicable) continue;
            if (action == BackgroundAction.None) continue;

            var decision = _executor.Apply(fact, action, GameModeOutcome.Applicable, action, Settings);
            lock (_gate)
            {
                _rows.RemoveAll(r => r.Pid == fact.Pid);
                _rows.Add(new GameModeAffectedRow(fact.Name, fact.Pid, Label(category), Label(decision.Planned), OutcomeLabel(decision.Outcome), decision.Message, fact.CreationTicks));
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
                var summary = _executor.RestoreAll(reason);
                watch.Stop();
                lock (_gate)
                {
                    _activeGame = string.Empty;
                    _activeGamePid = 0;
                    _activeGameTicks = 0;
                    _state = GameModeState.Idle;
                    _loggedSkips.Clear();
                    ApplyRestoreResultLocked();
                }
                _log.Add("-", "Game Mode", "Release", summary.Failed > 0 ? "Could not restore" : "Restored and verified",
                    summary.Failed > 0
                        ? $"Released in {watch.ElapsedMilliseconds} ms with {summary.Failed} process(es) still held after {reason}"
                        : $"Released in {watch.ElapsedMilliseconds} ms ({summary.Restored} process(es)) after {reason}");
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _state = GameModeState.Idle;
                    _activeGame = string.Empty;
                    _activeGamePid = 0;
                    _activeGameTicks = 0;
                }
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

    private GameModeRestoreSummary ReleaseNow(string reason)
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _rescanTimer?.Dispose();
            _generation++;
            _state = GameModeState.Idle;
            _activeGame = string.Empty;
            _activeGamePid = 0;
            _activeGameTicks = 0;
            _loggedSkips.Clear();
        }
        var summary = _executor.RestoreAll(reason);
        lock (_gate) ApplyRestoreResultLocked();
        Raise();
        return summary;
    }

    private void ApplyRestoreResultLocked()
    {
        var remaining = _journal.Snapshot().Select(e => (e.Pid, e.CreationTicks)).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (remaining.Contains((_rows[i].Pid, _rows[i].CreationTicks))) continue;
            _rows.RemoveAt(i);
        }
        for (int i = 0; i < _rows.Count; i++)
            _rows[i] = _rows[i] with { State = "Could not restore", Message = "Still held; it stays in the journal" };
    }

    private void PruneRows()
    {
        lock (_gate) _rows.RemoveAll(r => !GameModeExecutor.SameInstance(r.Pid, r.CreationTicks));
    }

    private void PruneJournal()
    {
        foreach (var entry in _journal.Snapshot())
            if (!GameModeExecutor.SameInstance(entry.Pid, entry.CreationTicks))
                _journal.Remove(entry.Pid, entry.CreationTicks);
    }

    private bool FindFocusedGame(out int pid, out GameModeGameEntry? game)
    {
        pid = 0;
        game = null;
        var hwnd = NativeMethods.GameMode.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && ForegroundWatcher.TryResolveForeground(hwnd, out int resolved, out string name, out string? imagePath))
        {
            if (resolved != Environment.ProcessId)
            {
                var current = FindGame(resolved, name, imagePath);
                if (current != null)
                {
                    pid = resolved;
                    game = current;
                    return true;
                }
            }
        }

        int last;
        long ticks;
        lock (_gate)
        {
            last = _lastForeignPid;
            ticks = _lastForeignTicks;
        }
        if (last == 0 || !GameModeExecutor.SameInstance(last, ticks)) return false;
        try
        {
            using var process = Process.GetProcessById(last);
            var match = FindGame(last, process.ProcessName + ".exe", ProcessPath(last));
            if (match == null) return false;
            pid = last;
            game = match;
            return true;
        }
        catch
        {
            return false;
        }
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
            var summary = executor.RestoreAll("kaliteConfig exited unexpectedly");
            log.Add("-", "Watchdog", "Recovery", summary.Failed > 0 ? "Could not restore" : summary.Restored > 0 ? "Restored and verified" : "Already in that state",
                $"Watchdog restored {summary.Restored} process(es) after the app exited, {summary.Failed} could not be restored");
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
