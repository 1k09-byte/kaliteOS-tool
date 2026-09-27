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
using System.Text.Json;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>
/// Persistent per-thread Priority-boost preferences. NOT rules: a boost tick on
/// a thread row records the state that thread must keep ("boost on" or "boost
/// off") and it is re-applied automatically whenever the owning process
/// launches, whenever the list is rebuilt, and by the keeper sweep.
///
/// Both directions are persisted, not just "off". That matters because
/// <c>SetProcessPriorityBoost(process, disable: true)</c> silently applies
/// "disabled" to EVERY existing thread of that process (verified on this
/// build), and the process-level preference is re-applied on a 20 s cadence.
/// A thread the user explicitly re-enabled therefore used to be stomped back
/// off by the next sweep, with nothing on record to restore it - the toggle
/// flipped on for a moment and then reverted forever. Recording the enabled
/// state too makes the keeper re-assert it after the process-level write, so
/// per-thread choices win over the process-wide default.
///
/// Identity: threads have no stable TID across restarts, so a preference is
/// keyed by process name + thread identity (description or start address).
/// Only boost is touched - never priority, affinity or anything else.
/// Storage lives beside the rules file and is independent of TunerProfile.
/// </summary>
public sealed class BoostPreferenceService
{
    private sealed class BoostPref
    {
        public string Process { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string StartAddress { get; set; } = string.Empty;
        /// <summary>The state this thread must keep. False = boost forced
        /// off; True = boost forced back on even if the process-wide default
        /// says off. Records written by older builds only ever meant "off",
        /// which is exactly what the default of false preserves.</summary>
        public bool Enabled { get; set; }
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
        => GetPreference(processName, tid, description, startAddress) == false;

    /// <summary>
    /// The recorded state for this thread identity: true = boost must stay on,
    /// false = boost must stay off, null = never chosen (follow Windows).
    /// </summary>
    public bool? GetPreference(string processName, int tid, string description, string startAddress)
    {
        lock (_lock)
        {
            foreach (var p in _prefs)
            {
                if (!string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.Tid == tid && p.CreatorPid == Environment.ProcessId
                    && p.Description == description && p.StartAddress == startAddress)
                    return p.Enabled;
                if (IdentityMatches(p, description, startAddress))
                    return p.Enabled;
            }
            return null;
        }
    }

    private static bool IdentityMatches(BoostPref p, string description, string startAddress)
        => BoostPreferenceRules.IdentityMatches(p.Description, p.StartAddress, description, startAddress);

    /// <summary>Records "boost OFF for this thread, forever" and applies it immediately.</summary>
    public async Task SuppressAsync(string processName, int tid, string description, string startAddress)
    {
        await App.Current.ThreadTuning.SetBoostAsync((uint)tid, enabled: false);

        lock (_lock)
        {
            RemoveMatching(processName, tid, description, startAddress);
            _prefs.Add(MakePref(processName, tid, description, startAddress, enabled: false));
        }
        await SaveAsync();
    }

    /// <summary>
    /// Records "boost ON for this thread" and applies it immediately. The
    /// record is kept (rather than deleted) so the keeper sweep can re-assert
    /// it after the process-wide preference is re-applied - that re-apply
    /// turns boost off on every existing thread of the process.
    /// </summary>
    public async Task RestoreAsync(string processName, int tid, string description, string startAddress)
    {
        await App.Current.ThreadTuning.SetBoostAsync((uint)tid, enabled: true);

        lock (_lock)
        {
            RemoveMatching(processName, tid, description, startAddress);
            _prefs.Add(MakePref(processName, tid, description, startAddress, enabled: true));
        }
        await SaveAsync();
    }

    /// <summary>Drops the recorded state so the thread follows Windows again.</summary>
    public async Task ClearAsync(string processName, int tid, string description, string startAddress)
    {
        bool removed;
        lock (_lock)
        {
            removed = RemoveMatching(processName, tid, description, startAddress);
        }
        if (removed) await SaveAsync();
    }

    private BoostPref MakePref(string processName, int tid, string description, string startAddress, bool enabled)
        => new()
        {
            Process = processName,
            Description = description ?? string.Empty,
            StartAddress = startAddress ?? string.Empty,
            Enabled = enabled,
            Tid = tid,
            CreatorPid = Environment.ProcessId,
        };

    /// <summary>Caller holds <see cref="_lock"/>. Returns true when anything went.</summary>
    private bool RemoveMatching(string processName, int tid, string description, string startAddress)
        => _prefs.RemoveAll(p =>
            string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase)
            && (p.Tid == tid || IdentityMatches(p, description, startAddress))) > 0;

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

        var threads = await ThreadQueryService.ListThreadsAsync(pid);
        return await ApplyToThreadsAsync(mine, threads);
    }

    /// <summary>
    /// Re-applies every stored per-thread state to the running processes that
    /// own them. Called from the keeper sweep: a thread Windows re-enabled (or
    /// that a process-wide disable just switched off), or one created after the
    /// process-start event, goes back to what the user chose instead of
    /// silently drifting away from it.
    /// </summary>
    public async Task<int> ApplyToRunningProcessesAsync()
    {
        List<string> names;
        lock (_lock)
        {
            names = _prefs.Select(p => p.Process)
                          .Where(n => !string.IsNullOrWhiteSpace(n))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }
        if (names.Count == 0) return 0;

        int applied = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                applied += await ApplyToProcessAsync(proc.Id, proc.ProcessName);
            }
            catch
            {
                // A process that exits mid-sweep is not an error.
            }
            finally
            {
                proc.Dispose();
            }
        }
        return applied;
    }

    private async Task<int> ApplyToThreadsAsync(List<BoostPref> mine, List<LiveThreadInfo> threads)
    {
        int applied = 0;
        foreach (var t in threads)
        {
            // Re-assert the RECORDED state, not a hardcoded "off": a thread the
            // user switched back on must be re-enabled here, because the
            // process-wide preference applied just before this sweep turned
            // boost off on every thread of the process.
            bool? wanted = BoostPreferenceRules.ResolveDesiredState(
                mine.Select(p => (p.Description, p.StartAddress, p.Enabled)),
                t.Description, t.StartAddress);
            if (!wanted.HasValue) continue;
            try
            {
                await App.Current.ThreadTuning.SetBoostAsync((uint)t.Tid, enabled: wanted.Value);
                applied++;
            }
            catch
            {
            }
        }
        return applied;
    }
}
