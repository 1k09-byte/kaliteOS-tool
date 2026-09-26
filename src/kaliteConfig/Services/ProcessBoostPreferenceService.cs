// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
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
/// Persistent per-PROCESS Priority-boost preference - the process-wide twin of
/// <see cref="BoostPreferenceService"/> (which covers single threads).
///
/// Unticking the boost box in the Processes list writes
/// <c>SetProcessPriorityBoost</c> on every live instance of that process AND
/// records the choice here. The record is re-applied when the process launches
/// again and by the keeper sweep, which is what makes the tick box permanent
/// instead of a one-shot that Windows forgets on the next restart.
///
/// Only priority boost is touched - never priority class, affinity or EcoQoS.
/// </summary>
public sealed class ProcessBoostPreferenceService
{
    private sealed class Entry
    {
        /// <summary>Process name, normalised without the ".exe" suffix.</summary>
        public string Process { get; set; } = string.Empty;
        /// <summary>False = boost must stay disabled for this process, forever.</summary>
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

    /// <summary>
    /// Writes the process-wide boost flag for one PID. Assigned by the app at
    /// startup (see <c>App.InitializeWatcherAsync</c>); left null here on
    /// purpose so this service compiles and tests without the WinUI application
    /// object - with no applier the preference is still recorded, it simply has
    /// no live process to write to.
    /// </summary>
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
        catch (Exception ex)
        {
            // A corrupt preference file must never block tuning; start empty.
            Debug.WriteLine($"ProcessBoostPreferenceService load: {ex.Message}");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessBoostPreferenceService save: {ex.Message}");
        }
    }

    /// <summary>Stored preference for a process, or null when it was never set.</summary>
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

    /// <summary>Short label for tooltips/status lines.</summary>
    public string Describe(string? processName) => GetPreference(processName) switch
    {
        true => "Priority boost kept enabled",
        false => "Priority boost kept disabled",
        _ => "Windows default boost",
    };

    /// <summary>
    /// Records the choice and applies it to every running instance right away,
    /// so the tick box acts immediately and stays that way afterwards.
    /// Returns the number of live processes it touched.
    /// </summary>
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
                // A process that exits mid-sweep is not an error.
            }
            finally
            {
                proc.Dispose();
            }
        }
        return applied;
    }

    /// <summary>Clears the stored preference (back to the Windows default).</summary>
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

    /// <summary>Applies the stored preference to one live process. False when the
    /// process has no preference or Windows refused the write.</summary>
    public async Task<bool> ApplyToProcessAsync(int pid, string? processName)
    {
        bool? wanted = GetPreference(processName);
        return wanted.HasValue && await ApplyAsync(pid, wanted.Value);
    }

    /// <summary>
    /// Re-applies every stored preference to the running processes that match.
    /// Called when a process launches and from the keeper sweep.
    /// </summary>
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
            // Protected / elevated / already-exited processes refuse the write.
            return false;
        }
    }
}
