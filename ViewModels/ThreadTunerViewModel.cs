using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.Native;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using System.IO;
using System.Text.Json;

namespace kaliteConfig.ViewModels;

public sealed partial class ThreadTunerViewModel : ObservableObject
{
    private readonly ProcessTuningService _tuning;
    private readonly CpuSetService _cpuSets;
    private readonly ThreadTuningService _threads;
    private readonly ProfileWatcherService _profiles;
    private readonly DispatcherQueue _dispatcher = null!;
    private readonly DispatcherQueueTimer _timer = null!;
    private bool _refreshing;
    private bool _initialThreadsLoaded;

    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "threadtuner-settings.json");
    private bool _settingsLoaded;

    private class ThreadTunerSettings
    {
        public bool HideUnnamedThreads { get; set; }
        public bool AutoDisableUnnamedBoosts { get; set; }
    }

    public ObservableCollection<TunerProcessRow> Processes { get; } = new();
    /// <summary>Filtered view of Processes driven by RulesFilter (search box).
    /// Rebuilt whenever the filter changes or a process row is added/removed.</summary>
    public ObservableCollection<TunerProcessRow> DisplayedProcesses { get; } = new();
    public ObservableCollection<TunerProfile> Profiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProfiles { get; } = new();

    /// <summary>Full flat list of thread-boost rows (Thread Tune tab).</summary>
    public ObservableCollection<ThreadBoostRow> ThreadBoostRows { get; } = new();
    /// <summary>Filtered view driven by RulesFilter search box.</summary>
    public ObservableCollection<ThreadBoostRow> DisplayedThreadBoostRows { get; } = new();
    /// <summary>Set by the page when the Thread Tune tab is selected — gates the expensive per-thread scan.</summary>
    [ObservableProperty]
    public partial bool IsThreadTuneTabActive { get; set; }

    [ObservableProperty]
    public partial bool IsThreadTuneLoading { get; set; }

    [ObservableProperty]
    public partial string ThreadTuneLoadingText { get; set; } = "Scanning system threads...";

    [ObservableProperty]
    public partial bool HideUnnamedThreads { get; set; }

    partial void OnHideUnnamedThreadsChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveSettingsAsync();
        RefreshDisplayedThreadBoostRows();
    }

    [ObservableProperty]
    public partial bool AutoDisableUnnamedBoosts { get; set; }

    partial void OnAutoDisableUnnamedBoostsChanged(bool value)
    {
        if (_settingsLoaded) _ = SaveSettingsAsync();
        if (value)
        {
            _ = Task.Run(() => 
            {
                var rowsToDisable = ThreadBoostRows.Where(r => !r.IsProtected && r.BoostEnabled && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var r in rowsToDisable)
                {
                    try
                    {
                        using var th = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, (uint)r.Tid);
                        if (!th.IsInvalid)
                        {
                            NativeMethods.Priority.SetThreadPriorityBoost(th, true);
                            _dispatcher.TryEnqueue(() => r.BoostEnabled = false);
                        }
                    }
                    catch { }
                }
            });
        }
        else
        {
            _ = Task.Run(() => 
            {
                var rowsToRestore = ThreadBoostRows.Where(r => !r.IsProtected && !r.BoostEnabled && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var r in rowsToRestore)
                {
                    try
                    {
                        using var th = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, (uint)r.Tid);
                        if (!th.IsInvalid)
                        {
                            NativeMethods.Priority.SetThreadPriorityBoost(th, false); // false = enable boost
                            _dispatcher.TryEnqueue(() => r.BoostEnabled = true);
                        }
                    }
                    catch { }
                }
            });
        }
    }

    private readonly HashSet<int> _seenUnnamedTids = new HashSet<int>();

    [ObservableProperty]
    public partial TunerProcessRow? SelectedProcess { get; set; }

    [ObservableProperty]
    public partial TunerProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string RulesFilter { get; set; } = string.Empty;

    public ThreadTunerViewModel()
    {
        // Safe defaults for designer / DI
        _tuning = App.Current.ProcessTuning;
        _cpuSets = App.Current.CpuSets;
        _threads = App.Current.ThreadTuning;
        _profiles = App.Current.ProfileWatcher;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        if (_dispatcher != null)
        {
            _timer = _dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += async (s, e) =>
            {
                // Never let a slow tick overlap the next one: two refreshes at
                // once double the UI churn and make the list feel frozen.
                if (_refreshing)
                {
                    return;
                }

                _refreshing = true;
                try
                {
                    await LoadProcessesAsync();
                    await RefreshCpuAsync();
                    if (IsThreadTuneTabActive)
                    {
                        await LoadThreadBoostRowsAsync();
                    }
                }
                finally
                {
                    _refreshing = false;
                }
            };
            _timer.Start();
            
            _ = LoadProcessesAsync();
        }

        // Setup base profiles binding
        foreach (var p in _profiles.ActiveProfiles)
        {
            Profiles.Add(p);
        }
        RefreshDisplayedProfiles();

        _profiles.RulesChanged += (_, _) => SyncProfiles();
        
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var text = await File.ReadAllTextAsync(_settingsPath);
                var settings = JsonSerializer.Deserialize<ThreadTunerSettings>(text);
                if (settings != null)
                {
                    HideUnnamedThreads = settings.HideUnnamedThreads;
                    AutoDisableUnnamedBoosts = settings.AutoDisableUnnamedBoosts;
                }
            }
        }
        catch { }
        finally { _settingsLoaded = true; }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new ThreadTunerSettings
            {
                HideUnnamedThreads = HideUnnamedThreads,
                AutoDisableUnnamedBoosts = AutoDisableUnnamedBoosts
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Reconciles the bound collections with the service store. Always runs
    /// on the UI thread (the service raises RulesChanged there).
    /// </summary>
    public void SyncProfiles()
    {
        var selected = SelectedProfile;
        Profiles.Clear();
        foreach (var p in _profiles.ActiveProfiles)
        {
            Profiles.Add(p);
        }
        if (selected != null && Profiles.Contains(selected))
        {
            SelectedProfile = selected;
        }
        else
        {
            SelectedProfile = null;
        }
        RefreshDisplayedProfiles();
    }

    partial void OnRulesFilterChanged(string value)
    {
        RefreshDisplayedProfiles();
        RefreshDisplayedThreadBoostRows();
    }

    /// <summary>Search matches process name and PID on the Processes tab, and
    /// rule name/pattern on the Rules tab — one box serves both tabs.</summary>
    public void RefreshDisplayedProcesses()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        var matching = new List<TunerProcessRow>();
        foreach (var p in Processes)
        {
            if (filter.Length == 0
                || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Pid.ToString().Contains(filter, StringComparison.Ordinal))
            {
                matching.Add(p);
            }
        }
        
        var matchingPids = new HashSet<int>();
        foreach (var m in matching) matchingPids.Add(m.Pid);

        for (int i = DisplayedProcesses.Count - 1; i >= 0; i--)
        {
            if (!matchingPids.Contains(DisplayedProcesses[i].Pid))
            {
                DisplayedProcesses.RemoveAt(i);
            }
        }

        for (int i = 0; i < matching.Count; i++)
        {
            var p = matching[i];
            if (i < DisplayedProcesses.Count)
            {
                if (DisplayedProcesses[i].Pid != p.Pid)
                {
                    int currentIdx = -1;
                    for(int j = i + 1; j < DisplayedProcesses.Count; j++) {
                        if (DisplayedProcesses[j].Pid == p.Pid) { currentIdx = j; break; }
                    }
                    if (currentIdx != -1) 
                        DisplayedProcesses.Move(currentIdx, i);
                    else 
                        DisplayedProcesses.Insert(i, p);
                }
            }
            else
            {
                DisplayedProcesses.Add(p);
            }
        }
    }

    public void RefreshDisplayedProfiles()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        DisplayedProfiles.Clear();
        foreach (var p in Profiles)
        {
            if (filter.Length == 0
                || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Pattern.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                DisplayedProfiles.Add(p);
            }
        }
        if (SelectedProfile != null && !DisplayedProfiles.Contains(SelectedProfile))
        {
            SelectedProfile = null;
        }
    }

    [RelayCommand]
    private async Task LoadProcessesAsync()
    {
        var active = await _tuning.ListProcessesAsync();
        
        _dispatcher.TryEnqueue(() => 
        {
            var livePids = new HashSet<int>(active.Select(p => p.Pid));
            bool changed = false;

            foreach (var p in active)
            {
                var existing = Processes.FirstOrDefault(x => x.Pid == p.Pid);
                if (existing is null)
                {
                    // New process: take the row as enumerated.
                    Processes.Add(p);
                    changed = true;
                    if (!string.IsNullOrEmpty(p.Path))
                    {
                        _ = ExtractIconAsync(p);
                    }
                }
                else
                {
                    if (existing.PriorityText != p.PriorityText) existing.PriorityText = p.PriorityText;
                    if (existing.PriorityValue != p.PriorityValue) existing.PriorityValue = p.PriorityValue;
                    if (existing.AffinitySummary != p.AffinitySummary) existing.AffinitySummary = p.AffinitySummary;
                    if (existing.AffinityMask != p.AffinityMask) existing.AffinityMask = p.AffinityMask;
                    if (existing.EfficiencyMode != p.EfficiencyMode) existing.EfficiencyMode = p.EfficiencyMode;
                    if (existing.State != p.State) existing.State = p.State;
                    if (existing.Error != p.Error) existing.Error = p.Error;
                }
            }

            // Remove dead
            for (int i = Processes.Count - 1; i >= 0; i--)
            {
                if (!livePids.Contains(Processes[i].Pid))
                {
                    Processes.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed)
            {
                RefreshDisplayedProcesses();
            }
        });
    }

    private async Task ExtractIconAsync(TunerProcessRow row)
    {
        try
        {
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(row.Path);
            var tb = await sf.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (tb != null && tb.Size > 0)
            {
                _dispatcher.TryEnqueue(async () =>
                {
                    try 
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bmp.SetSourceAsync(tb);
                        row.AppIcon = bmp;
                    } catch { }
                });
            }
        }
        catch { }
    }

    private async Task RefreshCpuAsync()
    {
        if (Processes.Count == 0) return;
        
        // Pass the read-only projection directly to the tuning service
        await _tuning.SampleCpuAsync(Processes.ToList());
        _tuning.PruneCache(Processes.Select(p => p.Pid));
    }

    // ─── Thread Tune tab ───────────────────────────────────────────

    /// <summary>
    /// Scans every thread of every process and reads its priority-boost state.
    /// Differential update: existing rows are patched in-place so the ListView
    /// doesn't reset scroll position.
    /// </summary>
    public async Task LoadThreadBoostRowsAsync()
    {
        if (!_initialThreadsLoaded)
        {
            _dispatcher.TryEnqueue(() => IsThreadTuneLoading = true);
        }
        var allRows = await Task.Run(async () =>
        {
            var rows = new List<ThreadBoostRow>();
            try
            {
                Process[] procs;
                try { procs = Process.GetProcesses(); } catch { return rows; }

                foreach (var proc in procs)
                {
                    string procName;
                    int pid;
                    try { procName = proc.ProcessName; pid = proc.Id; }
                    catch { proc.Dispose(); continue; }

                    _dispatcher.TryEnqueue(() => ThreadTuneLoadingText = $"Scanning {procName}...");

                    if (pid <= 4)
                    {
                        proc.Dispose();
                        continue;
                    }

                    ProcessThreadCollection threads;
                    try { threads = proc.Threads; } catch { proc.Dispose(); continue; }

                    foreach (ProcessThread t in threads)
                    {
                        int tid;
                        try { tid = t.Id; } catch { continue; }

                        bool boost = true;
                        bool isProtected = true;
                        string desc = "(unnamed)";
                        
                        try
                        {
                            using var threadHandle = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.QueryInformation, false, (uint)tid);
                            if (!threadHandle.IsInvalid && NativeMethods.Priority.GetThreadPriorityBoost(threadHandle, out bool disabled))
                            {
                                boost = !disabled;
                                isProtected = false;
                            }
                        }
                        catch { }

                        try
                        {
                            desc = NativeSnapshotService.TryGetThreadDescription((uint)tid) ?? "(unnamed)";
                        }
                        catch { }

                        if (AutoDisableUnnamedBoosts && string.Equals(desc, "(unnamed)", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!_seenUnnamedTids.Contains(tid))
                            {
                                _seenUnnamedTids.Add(tid);
                                if (boost && !isProtected)
                                {
                                    try
                                    {
                                        using var th = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, (uint)tid);
                                        if (!th.IsInvalid)
                                        {
                                            NativeMethods.Priority.SetThreadPriorityBoost(th, true); // true = disable boost
                                            boost = false;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }

                        rows.Add(new ThreadBoostRow
                        {
                            Tid = tid,
                            Pid = pid,
                            ProcessName = procName.ToUpperInvariant(),
                            Description = desc,
                            BoostEnabled = boost,
                            IsProtected = isProtected
                        });
                    }
                    proc.Dispose();
                }
                return rows.OrderBy(r => r.ProcessName).ThenBy(r => r.Tid).ToList();
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("threadtune_crash.txt", ex.ToString());
                return rows;
            }
        });

        System.IO.File.WriteAllText("threadtune_count.txt", allRows.Count.ToString());

        _dispatcher.TryEnqueue(() =>
        {
            if (!_initialThreadsLoaded)
            {
                IsThreadTuneLoading = false;
                _initialThreadsLoaded = true;
            }
            var liveTids = new HashSet<int>(allRows.Select(r => r.Tid));

            // Remove dead threads
            for (int i = ThreadBoostRows.Count - 1; i >= 0; i--)
            {
                if (!liveTids.Contains(ThreadBoostRows[i].Tid))
                    ThreadBoostRows.RemoveAt(i);
            }

            // Upsert
            var existingByTid = new Dictionary<int, ThreadBoostRow>();
            foreach (var r in ThreadBoostRows) existingByTid[r.Tid] = r;

            foreach (var r in allRows)
            {
                if (existingByTid.TryGetValue(r.Tid, out var existing))
                {
                    // Patch in place — don't touch BoostEnabled if user is toggling
                    if (!existing.IsBusy && existing.BoostEnabled != r.BoostEnabled)
                        existing.BoostEnabled = r.BoostEnabled;
                    if (existing.ProcessName != r.ProcessName) existing.ProcessName = r.ProcessName;
                    if (existing.Description != r.Description) existing.Description = r.Description;
                    if (existing.IsProtected != r.IsProtected) existing.IsProtected = r.IsProtected;
                }
                else
                {
                    ThreadBoostRows.Add(r);
                }
            }

            RefreshDisplayedThreadBoostRows();
        });
    }

    public void RefreshDisplayedThreadBoostRows()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        DisplayedThreadBoostRows.Clear();
        foreach (var r in ThreadBoostRows)
        {
            if (HideUnnamedThreads && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (filter.Length == 0
                || r.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Tid.ToString().Contains(filter, StringComparison.Ordinal)
                || r.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                DisplayedThreadBoostRows.Add(r);
            }
        }
    }
}
