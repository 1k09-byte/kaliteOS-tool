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

public sealed class BoostPreferenceService
{
    private sealed class BoostPref
    {
        public string Process { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string StartAddress { get; set; } = string.Empty;
        public bool Enabled { get; set; }
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

    public bool IsSuppressed(string processName, int tid, string description, string startAddress)
        => GetPreference(processName, tid, description, startAddress) == false;

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

    private bool RemoveMatching(string processName, int tid, string description, string startAddress)
        => _prefs.RemoveAll(p =>
            string.Equals(p.Process, processName, StringComparison.OrdinalIgnoreCase)
            && (p.Tid == tid || IdentityMatches(p, description, startAddress))) > 0;

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

    public async Task<int> ApplyToRunningProcessesAsync()
    {
        HashSet<string> names;
        lock (_lock)
        {
            names = _prefs.Select(p => p.Process)
                          .Where(n => !string.IsNullOrWhiteSpace(n))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        if (names.Count == 0) return 0;

        int applied = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(proc.ProcessName)) continue;
                applied += await ApplyToProcessAsync(proc.Id, proc.ProcessName);
            }
            catch
            {
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
