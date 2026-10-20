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

public sealed class ProcessBoostPreferenceService
{
    private sealed class Entry
    {
        public string Process { get; set; } = string.Empty;
        public bool BoostEnabled { get; set; } = true;
    }

    private readonly object _lock = new();
    private readonly string _path;
    private List<Entry> _entries = new();
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public ProcessBoostPreferenceService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "process-boost-prefs.json");
        _ = LoadAsync();
    }

    public static ProcessBoostPreferenceService Instance { get; } = new();

    public static Func<int, bool, Task>? Applier { get; set; }

    private static string Normalize(string? processName)
    {
        string name = (processName ?? string.Empty).Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = await File.ReadAllTextAsync(_path);
            var loaded = JsonSerializer.Deserialize<List<Entry>>(text);
            if (loaded != null)
            {
                lock (_lock) _entries = loaded;
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            List<Entry> snapshot;
            lock (_lock) snapshot = _entries.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    public bool? GetPreference(string? processName)
    {
        string name = Normalize(processName);
        if (name.Length == 0) return null;
        lock (_lock)
        {
            var hit = _entries.FirstOrDefault(e => string.Equals(e.Process, name, StringComparison.OrdinalIgnoreCase));
            return hit?.BoostEnabled;
        }
    }

    public string Describe(string? processName) => GetPreference(processName) switch
    {
        true => "Priority boost kept enabled",
        false => "Priority boost kept disabled",
        _ => "Windows default boost",
    };

    public async Task<int> SetAsync(string? processName, bool boostEnabled)
    {
        string name = Normalize(processName);
        if (name.Length == 0) return 0;

        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e.Process, name, StringComparison.OrdinalIgnoreCase));
            _entries.Add(new Entry { Process = name, BoostEnabled = boostEnabled });
        }
        await SaveAsync();

        int applied = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!string.Equals(Normalize(proc.ProcessName), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (await ApplyAsync(proc.Id, boostEnabled)) applied++;
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

    public async Task<bool> ClearAsync(string? processName)
    {
        string name = Normalize(processName);
        if (name.Length == 0) return false;

        bool removed;
        lock (_lock)
        {
            removed = _entries.RemoveAll(e => string.Equals(e.Process, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) await SaveAsync();
        return removed;
    }

    public async Task<bool> ApplyToProcessAsync(int pid, string? processName)
    {
        bool? wanted = GetPreference(processName);
        return wanted.HasValue && await ApplyAsync(pid, wanted.Value);
    }

    public async Task<int> ApplyToRunningProcessesAsync()
    {
        List<Entry> entries;
        lock (_lock) entries = _entries.ToList();
        if (entries.Count == 0) return 0;

        var wanted = entries.ToDictionary(e => e.Process, e => e.BoostEnabled, StringComparer.OrdinalIgnoreCase);

        int applied = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!wanted.TryGetValue(Normalize(proc.ProcessName), out bool boostEnabled)) continue;
                if (await ApplyAsync(proc.Id, boostEnabled)) applied++;
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

    private static async Task<bool> ApplyAsync(int pid, bool boostEnabled)
    {
        var applier = Applier;
        if (applier is null) return false;
        try
        {
            await applier(pid, boostEnabled);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
