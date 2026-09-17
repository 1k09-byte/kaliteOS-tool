using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>
/// Persistent per-thread Priority-boost preferences. NOT rules: an unticked
/// boost checkbox on a thread row records "this thread must never have
/// priority boost" and the preference is re-applied automatically whenever
/// the owning process launches (or the tuner starts mid-run). Ticking it
/// again removes the preference and re-enables boost immediately.
///
/// Identity: threads have no stable TID across restarts, so a preference is
/// keyed by process name + thread identity (description or start address).
/// Only boost is touched — never priority, affinity or anything else.
/// Storage lives beside the rules file and is independent of TunerProfile.
/// </summary>
public sealed class BoostPreferenceService
{
    private sealed class BoostPref
    {
        public string Process { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string StartAddress { get; set; } = string.Empty;
        /// <summary>Sticky TID for the session the pref was created in, so
        /// unnamed threads (no description/address) can still be matched
        /// while the process instance is alive.</summary>
        public int Tid { get; set; }
        public int CreatorPid { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _path;
    private List<BoostPref> _prefs = new();
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public BoostPreferenceService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "boost-prefs.json");
        _ = LoadAsync();
    }

    public static BoostPreferenceService Instance { get; } = new();

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = await File.ReadAllTextAsync(_path);
            var loaded = JsonSerializer.Deserialize<List<BoostPref>>(text);
            if (loaded != null)
            {
                lock (_lock) _prefs = loaded;
            }
        }
        catch
        {
            // A corrupt pref file must never block tuning; start empty.
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            List<BoostPref> snapshot;
            lock (_lock) snapshot = _prefs.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
        }
    }

    /// <summary>True when boost is suppressed for this thread identity.</summary>
    public bool IsSuppressed(string processName, int tid, string description, string startAddress)
    {
        lock (_lock)
        {
            foreach (var p in _prefs)
            {
                if (!string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.Tid == tid && p.CreatorPid == Environment.ProcessId && p.Description == description && p.StartAddress == startAddress)
                    return true;
                if (IdentityMatches(p, description, startAddress))
                    return true;
            }
            return false;
        }
    }

    private static bool IdentityMatches(BoostPref p, string description, string startAddress)
    {
        bool HasText(string s) => !string.IsNullOrWhiteSpace(s);
        if (HasText(p.Description) && string.Equals(p.Description, description, StringComparison.OrdinalIgnoreCase))
            return true;
        if (HasText(p.StartAddress) && HasText(startAddress) &&
            string.Equals(p.StartAddress, startAddress, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Records "boost OFF for this thread, forever" and applies it immediately.</summary>
    public async Task SuppressAsync(string processName, int tid, string description, string startAddress)
    {
        await App.Current.ThreadTuning.SetBoostAsync((uint)tid, enabled: false);

        lock (_lock)
        {
            _prefs.RemoveAll(p =>
                string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase)
                && (p.Tid == tid || IdentityMatches(p, description, startAddress)));
            _prefs.Add(new BoostPref
            {
                Process = processName,
                Description = description ?? string.Empty,
                StartAddress = startAddress ?? string.Empty,
                Tid = tid,
                CreatorPid = Environment.ProcessId,
            });
        }
        await SaveAsync();
    }

    /// <summary>Removes the suppression and re-enables boost immediately.</summary>
    public async Task RestoreAsync(string processName, int tid, string description, string startAddress)
    {
        await App.Current.ThreadTuning.SetBoostAsync((uint)tid, enabled: true);

        lock (_lock)
        {
            _prefs.RemoveAll(p =>
                string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase)
                && (p.Tid == tid || IdentityMatches(p, description, startAddress)));
        }
        await SaveAsync();
    }

    /// <summary>Re-applies every stored suppression that matches threads of one
    /// live process instance. Called from the watcher on process start and on
    /// thread-list refresh. Returns the number of threads touched.</summary>
    public async Task<int> ApplyToProcessAsync(int pid, string processName)
    {
        List<BoostPref> mine;
        lock (_lock)
        {
            mine = _prefs.Where(p => string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (mine.Count == 0) return 0;

        int applied = 0;
        var threads = await ThreadQueryService.ListThreadsAsync(pid);
        foreach (var t in threads)
        {
            bool hit = mine.Any(p =>
                IdentityMatches(p, t.Description, t.StartAddress));
            if (!hit) continue;
            try
            {
                await App.Current.ThreadTuning.SetBoostAsync((uint)t.Tid, enabled: false);
                applied++;
            }
            catch
            {
            }
        }
        return applied;
    }
}
