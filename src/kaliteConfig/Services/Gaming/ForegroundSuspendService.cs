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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Services;

namespace kaliteConfig.Services;

/// <summary>
/// Image-3 style game mode: when a listed game is in the foreground, all
/// background noise (launchers, overlays, updaters) is SUSPENDED; when you
/// switch back, everything resumes. Nothing is terminated, everything is
/// reversible - including across an app crash, via the on-disk journal:
/// any PID recorded as suspended is resumed on next launch when it still
/// matches, and ResumeAll runs on app exit.
/// </summary>
public sealed class ForegroundSuspendService : IDisposable
{
    private readonly ProcessTuningService _tuning;
    private readonly object _gate = new();
    private readonly Dictionary<int, SuspendedEntry> _suspended = new();
    private readonly HashSet<string> _games = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _watcher;
    private bool _auto;
    private string? _activeGame;
    private int _tickBusy;
    private bool _disposed;

    public event Action? StatusChanged;

    private string _status = "No game in foreground.";
    public string StatusText
    {
        get { lock (_gate) return _status; }
        private set { lock (_gate) _status = value; try { StatusChanged?.Invoke(); } catch { } }
    }

    public int SuspendedCount { get { lock (_gate) return _suspended.Count; } }

    public IReadOnlyList<string> GameExes
    {
        get { lock (_gate) return _games.OrderBy(g => g).ToList(); }
    }

    public bool Auto
    {
        get { lock (_gate) return _auto; }
        set
        {
            lock (_gate)
            {
                if (_auto == value) return;
                _auto = value;
            }
            SaveSettings();
            if (value) StartWatcher();
            else StopWatcher(resume: true);
        }
    }

    private sealed class SuspendedEntry
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime WhenUtc { get; set; }
    }

    private sealed class SuspendSettings
    {
        public List<string> Games { get; set; } = new();
        public bool Auto { get; set; }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "suspend-mode.json");

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "suspend-journal.json");

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public ForegroundSuspendService(ProcessTuningService tuning)
    {
        _tuning = tuning;
        LoadSettings();
        // Crash orphans: a kill between suspend and resume leaves frozen apps.
        // Resume anything still matching on a worker thread - never block startup.
        _ = Task.Run(async () =>
        {
            try { await ResumeJournalOrphansAsync().ConfigureAwait(false); }
            catch { }
        });
        if (Auto) StartWatcher();
    }

    // ─── game list ──────────────────────────────────────────────

    public void AddGame(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return;
        string norm = NormalizeExe(exeName);
        lock (_gate)
        {
            if (!_games.Add(norm)) return;
        }
        SaveSettings();
        StatusText = $"{norm} added to the game list.";
    }

    public void RemoveGame(string exeName)
    {
        lock (_gate)
        {
            _games.Remove(NormalizeExe(exeName));
        }
        SaveSettings();
        StatusText = $"{exeName} removed from the game list.";
    }

    public static string NormalizeExe(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name;
    }

    // ─── manual controls ────────────────────────────────────────

    /// <summary>Suspends background now for the hinted game, else the foreground game.</summary>
    public async Task<int> SuspendBackgroundNowAsync(string? gameExeHint = null)
    {
        string? game = gameExeHint != null ? NormalizeExe(gameExeHint) : ForegroundExe();
        if (game == null)
        {
            StatusText = "No game in foreground.";
            return 0;
        }
        var targets = BuildBackgroundList(game);
        int n = 0;
        foreach (var (pid, ticks, name) in targets)
        {
            if (await SuspendOneAsync(pid, ticks, name).ConfigureAwait(false)) n++;
        }
        lock (_gate) _activeGame = game;
        StatusText = n > 0
            ? $"Suspended {n} background process(es) for {game}."
            : $"Nothing to suspend for {game}.";
        return n;
    }

    public async Task ResumeAllAsync()
    {
        List<SuspendedEntry> copy;
        lock (_gate)
        {
            copy = _suspended.Values.ToList();
            _suspended.Clear();
            _activeGame = null;
        }
        SaveJournal(copy);
        int n = 0;
        foreach (var e in copy)
        {
            if (await ResumeOneAsync(e).ConfigureAwait(false)) n++;
        }
        SaveJournal(Array.Empty<SuspendedEntry>());
        StatusText = n > 0 ? $"Resumed {n} process(es)." : "Nothing suspended.";
    }

    /// <summary>Synchronous variant for the app-exit path (no Task.Run hop).</summary>
    public void ResumeAllSync()
    {
        try
        {
            List<SuspendedEntry> copy;
            lock (_gate)
            {
                copy = _suspended.Values.ToList();
                _suspended.Clear();
                _activeGame = null;
            }
            // Journal first: a kill mid-resume still leaves recoverable entries.
            SaveJournal(copy);
            foreach (var e in copy)
            {
                try { ResumePidSync(e.Pid, e.StartTicks); } catch { }
            }
            SaveJournal(Array.Empty<SuspendedEntry>());
        }
        catch { }
    }

    /// <summary>Counts suspendable background processes without touching anything.</summary>
    public int RefreshSuspendableCount(string? gameExeHint = null)
    {
        string? game = gameExeHint != null ? NormalizeExe(gameExeHint) : ForegroundExe();
        int n = BuildBackgroundList(game).Count;
        StatusText = game == null
            ? "No game in foreground."
            : $"{n} background process(es) would suspend for {game}.";
        return n;
    }

    // ─── auto watcher ───────────────────────────────────────────

    private void StartWatcher()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _watcher ??= new Timer(WatchTick, null, 1000, 1000);
        }
    }

    private void StopWatcher(bool resume)
    {
        Timer? t;
        lock (_gate)
        {
            t = _watcher;
            _watcher = null;
        }
        try { t?.Dispose(); } catch { }
        if (resume) _ = ResumeAllAsync();
        else StatusText = "Auto suspend off.";
    }

    private void WatchTick(object? _)
    {
        if (Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0) return;
        try
        {
            string? fg = ForegroundExe();
            string? active;
            lock (_gate) active = _activeGame;
            bool listed = fg != null && IsGameListed(fg);
            if (listed && !string.Equals(active, fg, StringComparison.OrdinalIgnoreCase))
            {
                // Switched (back) into a listed game: resume previous, suspend new set.
                ResumeAllSync();
                var targets = BuildBackgroundList(fg);
                int n = 0;
                foreach (var (pid, ticks, name) in targets)
                {
                    try
                    {
                        if (SuspendPidSync(pid, ticks, record: true)) n++;
                    }
                    catch { }
                }
                lock (_gate) _activeGame = fg;
                StatusText = n > 0 ? $"Suspended {n} background process(es) for {fg}." : $"Nothing to suspend for {fg}.";
            }
            else if (!listed && active != null)
            {
                ResumeAllSync();
                StatusText = "No game in foreground.";
            }
        }
        catch { }
        finally { Volatile.Write(ref _tickBusy, 0); }
    }

    private bool IsGameListed(string exe)
    {
        lock (_gate) return _games.Contains(exe);
    }

    // ─── core ───────────────────────────────────────────────────

    private static string? ForegroundExe()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName + ".exe";
        }
        catch { return null; }
    }

    private List<(int Pid, long Ticks, string Name)> BuildBackgroundList(string? gameExe)
    {
        var list = new List<(int, long, string)>();
        int self = Process.GetCurrentProcess().Id;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                int pid;
                string name;
                try
                {
                    pid = proc.Id;
                    name = proc.ProcessName + ".exe";
                }
                catch { continue; }
                finally { try { proc.Dispose(); } catch { } }

                if (pid <= 4 || pid == self) continue;
                if (gameExe != null && name.Equals(gameExe, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_gate)
                {
                    if (_games.Contains(name)) continue; // other listed games are never noise
                }
                 if (ProcessTuningService.IsCritical(name, pid)) continue;
                 if (ProcessTuningService.IsSelf(pid)) continue;
                 long ticks;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited) continue;
                    ticks = p.StartTime.Ticks;
                }
                catch { continue; }
                lock (_gate)
                {
                    if (_suspended.TryGetValue(pid, out var existing) && existing.StartTicks == ticks)
                        continue; // already ours
                }
                list.Add((pid, ticks, name));
            }
        }
        catch { }
        return list;
    }

    private async Task<bool> SuspendOneAsync(int pid, long ticks, string name)
    {
        try
        {
            await _tuning.SuspendProcessAsync(pid).ConfigureAwait(false);
            lock (_gate) _suspended[pid] = new SuspendedEntry { Pid = pid, StartTicks = ticks, Name = name, WhenUtc = DateTime.UtcNow };
            SaveJournal(CurrentEntries());
            return true;
        }
        catch { return false; }
    }

    /// <summary>Synchronous suspend used by the watcher tick and exit path.</summary>
    private bool SuspendPidSync(int pid, long ticks, bool record)
    {
        try
        {
            string name;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited || p.StartTime.Ticks != ticks) return false;
                name = p.ProcessName + ".exe";
            }
            catch { return false; }
            foreach (uint tid in LiveThreadIds(pid))
            {
                try
                {
                    using var thread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SuspendResume, false, tid);
                    if (!thread.IsInvalid) NativeMethods.Threads.SuspendThread(thread);
                }
                catch { }
            }
            if (record)
            {
                lock (_gate) _suspended[pid] = new SuspendedEntry { Pid = pid, StartTicks = ticks, Name = name, WhenUtc = DateTime.UtcNow };
                SaveJournal(CurrentEntries());
            }
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> ResumeOneAsync(SuspendedEntry e)
    {
        try
        {
            if (!SameInstance(e.Pid, e.StartTicks)) return false;
            await _tuning.ResumeProcessAsync(e.Pid).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private void ResumePidSync(int pid, long ticks)
    {
        if (!SameInstance(pid, ticks)) return;
        foreach (uint tid in LiveThreadIds(pid))
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SuspendResume, false, tid);
                if (thread.IsInvalid) continue;
                // Drain suspend counts so a stray suspend can't wedge the process.
                for (int i = 0; i < 8; i++)
                {
                    uint prev = NativeMethods.Threads.ResumeThread(thread);
                    if (prev == 0 || prev == uint.MaxValue) break;
                }
            }
            catch { }
        }
    }

    private static bool SameInstance(int pid, long ticks)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited && proc.StartTime.Ticks == ticks;
        }
        catch { return false; }
    }

    private static List<uint> LiveThreadIds(int pid)
    {
        var ids = new List<uint>();
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread t in proc.Threads) ids.Add((uint)t.Id);
        }
        catch { }
        return ids;
    }

    private List<SuspendedEntry> CurrentEntries()
    {
        lock (_gate) return _suspended.Values.ToList();
    }

    // ─── persistence ────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("Games", out var games) && games.ValueKind == JsonValueKind.Array)
                    foreach (var g in games.EnumerateArray())
                        if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
                            lock (_gate) _games.Add(NormalizeExe(g.GetString()!));
                if (root.TryGetProperty("Auto", out var auto) &&
                    (auto.ValueKind == JsonValueKind.True || auto.ValueKind == JsonValueKind.False))
                    lock (_gate) _auto = auto.GetBoolean();
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            List<string> games;
            bool auto;
            lock (_gate)
            {
                games = _games.OrderBy(g => g).ToList();
                auto = _auto;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            using var fs = File.Create(SettingsPath);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteStartArray("Games");
            foreach (var g in games) w.WriteStringValue(g);
            w.WriteEndArray();
            w.WriteBoolean("Auto", auto);
            w.WriteEndObject();
        }
        catch { }
    }

    private static void SaveJournal(IReadOnlyList<SuspendedEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            using var fs = File.Create(JournalPath);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartArray();
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteNumber("Pid", e.Pid);
                w.WriteNumber("StartTicks", e.StartTicks);
                w.WriteString("Name", e.Name);
                w.WriteString("WhenUtc", e.WhenUtc.ToString("O"));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        catch { }
    }

    private async Task ResumeJournalOrphansAsync()
    {
        List<SuspendedEntry> orphans = new();
        try
        {
            if (!File.Exists(JournalPath)) return;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(JournalPath).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    orphans.Add(new SuspendedEntry
                    {
                        Pid = el.GetProperty("Pid").GetInt32(),
                        StartTicks = el.GetProperty("StartTicks").GetInt64(),
                        Name = el.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    });
                }
                catch { }
            }
        }
        catch { return; }
        if (orphans.Count == 0) return;
        int n = 0;
        foreach (var e in orphans)
        {
            try
            {
                if (SameInstance(e.Pid, e.StartTicks))
                {
                    await _tuning.ResumeProcessAsync(e.Pid).ConfigureAwait(false);
                    n++;
                }
            }
            catch { }
        }
        SaveJournal(Array.Empty<SuspendedEntry>());
        if (n > 0) StatusText = $"Resumed {n} process(es) left suspended by a previous session.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Timer? t;
        lock (_gate)
        {
            t = _watcher;
            _watcher = null;
        }
        try { t?.Dispose(); } catch { }
    }
}
