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
using kaliteConfig.ProcessOptimizer.ViewModels;

namespace kaliteConfig.ViewModels;

public sealed partial class ThreadTunerViewModel : ObservableObject
{
    /// <summary>
    /// Hard ceiling on process-tree depth. Real chains are 2-4 deep; the cap
    /// exists only so a PID cycle (parent chain that loops back on itself,
    /// possible while PIDs are being recycled) can't spin forever.
    /// </summary>
    private const int MaxTreeDepth = 24;

    private readonly ProcessTuningService _tuning;
    private readonly CpuSetService _cpuSets;
    private readonly ThreadTuningService _threads;
    private readonly ProfileWatcherService _profiles;
    private readonly DispatcherQueue _dispatcher = null!;
    private readonly DispatcherQueueTimer _timer = null!;

    public ProcessOptimizerViewModel Optimizer { get; }
    private bool _refreshing;
    private bool _initialThreadsLoaded;
    private DispatcherQueueTimer? _searchTimer;

    /// <summary>
    /// First-load state only, not the 2 s poll - a spinner on every tick would
    /// flicker forever.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessListLoadingVis))]
    public partial bool IsProcessListLoading { get; set; } = true;

    /// <summary>Overlay visibility for the loading flag.</summary>
    public Microsoft.UI.Xaml.Visibility IsProcessListLoadingVis =>
        IsProcessListLoading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>PIDs we already attempted an icon load for (avoids re-hitting
    /// disk every 2 s tick for icon-less pseudo-processes).</summary>
    private readonly HashSet<int> _iconTried = new();

    /// <summary>Cached fallback bitmap: the real wininit.exe icon. Icon-less
    /// pseudo-processes (System, Registry, ...) show this instead of the
    /// blank-page glyph, matching the wininit.exe row.</summary>
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _fallbackAppIcon;

    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "threadtuner-settings.json");

    private class ThreadTunerSettings
    {
        public bool ShowCounterDeltas { get; set; }
    }

    public ObservableCollection<TunerProcessRow> Processes { get; } = new();
    /// <summary>Filtered view of Processes driven by RulesFilter (search box).
    /// Rebuilt whenever the filter changes or a process row is added/removed.</summary>
    public ObservableCollection<TunerProcessRow> DisplayedProcesses { get; } = new();
    public ObservableCollection<TunerProfile> Profiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProfiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProcessProfiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedThreadProfiles { get; } = new();

    /// <summary>Full flat list of thread-boost rows (Thread Tune tab).</summary>
    public ObservableCollection<ThreadBoostRow> ThreadBoostRows { get; } = new();
    /// <summary>Filtered view driven by RulesFilter search box.</summary>
    public ObservableCollection<ThreadBoostRow> DisplayedThreadBoostRows { get; } = new();
    /// <summary>Set by the page when the Thread Tune tab is selected - gates the expensive per-thread scan.</summary>
    [ObservableProperty]
    public partial bool IsThreadTuneTabActive { get; set; }

    /// <summary>
    /// True only while the PRC page is actually on screen. The 2 s poll does a
    /// WMI process scan plus a CPU sample, which is wasted work - and needless
    /// UI churn - while the user is on any other page. Set from the page's
    /// OnNavigatedTo / OnNavigatedFrom.
    ///
    /// Defaulted to true so the list is populated on first construction, before
    /// any navigation event has fired.
    /// </summary>
    public bool IsPageVisible { get; set; } = true;

    /// <summary>
    /// True only while the window is the foreground window.
    ///
    /// <see cref="IsPageVisible"/> alone is not enough: this page stays "visible"
    /// when the app is minimised or sitting behind something else, so the poll kept
    /// walking every process - and, with the delta columns on, taking a full thread
    /// snapshot of every process in the system - with nobody looking. This is what
    /// made the counters appear to keep running in the background.
    /// </summary>
    public bool IsWindowActive { get; set; } = true;

    /// <summary>Both must hold before the poll does any work.</summary>
    private bool ShouldPoll => IsPageVisible && IsWindowActive;

    [ObservableProperty]
    public partial bool IsThreadTuneLoading { get; set; }

    [ObservableProperty]
    public partial string ThreadTuneLoadingText { get; set; } = "Scanning system threads...";

    /// <summary>
    /// Shows the Context switches / Cycles delta columns. Turning it off also
    /// stops collecting them: <see cref="ProcessTuningService.CollectCounters"/>
    /// is set from here, so the per-tick thread snapshot is skipped entirely
    /// rather than computed and thrown away.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCounterDeltasVis))]
    public partial bool ShowCounterDeltas { get; set; }

    /// <summary>Header cells for the counter columns collapse with the toggle.</summary>
    public Microsoft.UI.Xaml.Visibility ShowCounterDeltasVis =>
        ShowCounterDeltas ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnShowCounterDeltasChanged(bool value)
    {
        _tuning.CollectCounters = value;
        // Coming back on: the stored baseline is stale by however long the
        // toggle was off, so the first tick would show a huge bogus delta.
        if (value) _tuning.ResetCounterBaselines();
        // Re-mirror onto the rows so their columns collapse immediately.
        RefreshDisplayedProcesses();
        _ = SaveSettingsAsync();
    }

    /// <summary>PIDs the user has collapsed in the process tree.</summary>
    private readonly HashSet<int> _collapsed = new();

    /// <summary>Unnamed threads are always hidden; no toggle exists in the UI.</summary>
    public bool HideUnnamedThreads => true;

    public Task AutoDisableUnnamedBoostsAsync()
    {
        return Task.Run(() => 
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

    public Task RestoreUnnamedBoostsAsync()
    {
        return Task.Run(() => 
        {
            var rowsToRestore = ThreadBoostRows.Where(r => !r.IsProtected && !r.BoostEnabled && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in rowsToRestore)
            {
                try
                {
                    using var th = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, (uint)r.Tid);
                    if (!th.IsInvalid)
                    {
                        NativeMethods.Priority.SetThreadPriorityBoost(th, false);
                        _dispatcher.TryEnqueue(() => r.BoostEnabled = true);
                    }
                }
                catch { }
            }
        });
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
        Optimizer = new ProcessOptimizerViewModel(_dispatcher);
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

                // Benchmark capture in progress: the tuner's own polling
                // (WMI process scan + CPU sampling every 2 s) skews frametime
                // stats. Stand down until the capture finishes.
                if (BenchmarkViewModel.CaptureInProgress)
                {
                    return;
                }

                // Off-page OR window not in the foreground: skip the process scan
                // and CPU sampling entirely. The rules engine's own watchers are
                // separate from this poll and keep running, so nothing about rule
                // application depends on it.
                if (!ShouldPoll)
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
                ShowCounterDeltas = settings.ShowCounterDeltas;
            }
            }
            // No settings file yet: honor the default without writing one out.
            _tuning.CollectCounters = ShowCounterDeltas;
        }
        catch { }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new ThreadTunerSettings { ShowCounterDeltas = ShowCounterDeltas };
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
        if (_searchTimer == null && _dispatcher != null)
        {
            _searchTimer = _dispatcher.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                RefreshDisplayedProcesses();
                RefreshDisplayedProfiles();
                RefreshDisplayedThreadBoostRows();
            };
        }
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    /// <summary>Search matches process name and PID on the Processes tab, and
    /// rule name/pattern on the Rules tab - one box serves both tabs.</summary>
    /// <summary>
    /// Refreshes the process list right now, bypassing the 2 s poll. Used when
    /// navigating back into the page so the list is never a tick stale.
    /// Fire-and-forget: callers discard the task, and a failure here only means
    /// the next poll tick catches up.
    /// </summary>
    /// <summary>
    /// Called when the window comes back to the foreground. The poll stood down
    /// while hidden, so the counter baselines are stale by the whole hidden period
    /// and the first delta would read as one enormous jump. Resetting them makes
    /// the next tick measure from now instead of averaging over dead time.
    /// </summary>
    public void OnReturningToForeground()
    {
        _tuning.ResetCounterBaselines();
        _ = RefreshProcessesNowAsync();
    }

    public async Task RefreshProcessesNowAsync()
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            await LoadProcessesAsync();
            await RefreshCpuAsync();
        }
        catch { }
        finally
        {
            _refreshing = false;
        }
    }

    public void RefreshDisplayedProcesses()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        var matching = filter.Length == 0
            ? OrderAsTree(Processes)
            : FlattenMatches(filter);

        // Depth / HasChildren were recomputed by OrderAsTree above even though the
        // visible set may not have changed (a child can come or go between
        // ticks without the set itself changing).

        // Mirror the counter toggle onto every row, including filtered-out ones,
        // so the template binds per row and rehydrates correctly.
        bool showCounters = ShowCounterDeltas;
        foreach (var p in Processes)
        {
            if (p.ShowCounters != showCounters) p.ShowCounters = showCounters;
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

    /// <summary>
    /// Depth-first ordering that nests each process under its parent, skipping
    /// the subtree of a collapsed row. Returns the rows in display order.
    ///
    /// A process whose parent is not in the live list (already exited, or we
    /// couldn't read the parent PID) becomes a root. Cycles are broken by the
    /// <c>visited</c> set plus <see cref="MaxTreeDepth"/>.
    /// </summary>
    private List<TunerProcessRow> OrderAsTree(IEnumerable<TunerProcessRow> all)
    {
        var byPid = new Dictionary<int, TunerProcessRow>();
        var children = new Dictionary<int, List<TunerProcessRow>>();
        var roots = new List<TunerProcessRow>();

        // Materialized once: this runs on every 2 s tick and the walk below
        // iterates the source several times.
        var allRows = all as IReadOnlyList<TunerProcessRow> ?? all.ToList();

        foreach (var p in allRows)
        {
            byPid[p.Pid] = p;
            p.HasChildren = false;
        }

        foreach (var p in allRows)
        {
            if (p.ParentPid != 0 && p.ParentPid != p.Pid && byPid.TryGetValue(p.ParentPid, out var parent))
            {
                if (!children.TryGetValue(parent.Pid, out var list))
                    children[parent.Pid] = list = new List<TunerProcessRow>();
                list.Add(p);
                parent.HasChildren = true;
            }
            else
            {
                roots.Add(p);
            }
        }

        var ordered = new List<TunerProcessRow>(allRows.Count);
        var visited = new HashSet<int>();
        foreach (var root in roots)
        {
            Walk(root, 0, children, ordered, visited);
        }

        // Anything the walk didn't reach (shouldn't happen, but a cycle among
        // non-root rows could) still has to be visible rather than dropped.
        foreach (var p in allRows)
        {
            if (!visited.Contains(p.Pid))
            {
                p.Depth = 0;
                ordered.Add(p);
            }
        }

        return ordered;
    }

    private void Walk(TunerProcessRow row, int depth, Dictionary<int, List<TunerProcessRow>> children,
        List<TunerProcessRow> output, HashSet<int> visited)
    {
        if (!visited.Add(row.Pid)) return;

        row.Depth = depth;
        output.Add(row);

        if (!children.TryGetValue(row.Pid, out var kids) || depth >= MaxTreeDepth) return;

        row.IsExpanded = !_collapsed.Contains(row.Pid);
        if (!row.IsExpanded) return;

        foreach (var kid in kids)
        {
            Walk(kid, depth + 1, children, output, visited);
        }
    }

    /// <summary>
    /// Search result set. A search flattens the tree on purpose: matching a
    /// child should not require its parent to match too, and a filtered list
    /// that re-nests under rows which aren't shown reads as random indentation.
    /// </summary>
    private List<TunerProcessRow> FlattenMatches(string filter)
    {
        var matching = new List<TunerProcessRow>();
        foreach (var p in Processes)
        {
            if (p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Pid.ToString().Contains(filter, StringComparison.Ordinal))
            {
                p.Depth = 0;
                p.HasChildren = false;
                matching.Add(p);
            }
        }
        return matching;
    }

    /// <summary>Expands or collapses a process row's children in the tree.</summary>
    public void ToggleExpanded(TunerProcessRow row)
    {
        if (row is null || !row.HasChildren) return;
        if (!_collapsed.Add(row.Pid)) _collapsed.Remove(row.Pid);
        RefreshDisplayedProcesses();
    }

    public void RefreshDisplayedProfiles()
    {
        string filter = (RulesFilter ?? string.Empty).Trim();
        DisplayedProfiles.Clear();
        DisplayedProcessProfiles.Clear();
        DisplayedThreadProfiles.Clear();
        
        foreach (var p in Profiles)
        {
            if (filter.Length == 0
                || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Pattern.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                DisplayedProfiles.Add(p);
                // Rules tab is bound to DisplayedProcessProfiles: it must list
                // every profile, including thread-only rules (no process
                // actions). Those were previously filtered out and never appeared.
                DisplayedProcessProfiles.Add(p);

                if (p.ThreadRules.Count > 0)
                    DisplayedThreadProfiles.Add(p);
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
                    // Unconditional: a protected process has no readable Path but
                    // ExtractIconAsync falls back to System32, so gating on a
                    // non-empty Path is what kept locked processes icon-less.
                    _ = ExtractIconAsync(p);
                }
                else
                {
                    if (existing.PriorityText != p.PriorityText) existing.PriorityText = p.PriorityText;
                    if (existing.PriorityValue != p.PriorityValue) existing.PriorityValue = p.PriorityValue;
                    if (existing.AffinitySummary != p.AffinitySummary) existing.AffinitySummary = p.AffinitySummary;
                    if (existing.AffinityMask != p.AffinityMask) existing.AffinityMask = p.AffinityMask;
                    if (existing.EfficiencyMode != p.EfficiencyMode) existing.EfficiencyMode = p.EfficiencyMode;
                    if (existing.PriorityBoostText != p.PriorityBoostText) existing.PriorityBoostText = p.PriorityBoostText;
                    if (existing.BoostAllowed != p.BoostAllowed) existing.BoostAllowed = p.BoostAllowed;
                    if (existing.State != p.State) existing.State = p.State;
                    if (existing.Error != p.Error) existing.Error = p.Error;
                    // Backfill: rows that were icon-less before the wininit
                    // fallback existed get one retry so the blank glyph disappears.
                    if (existing.AppIcon == null && _iconTried.Add(existing.Pid))
                        _ = ExtractIconAsync(existing);
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

            // First real pass has landed: drop the spinner for good.
            if (IsProcessListLoading && Processes.Count > 0)
            {
                IsProcessListLoading = false;
            }
        });
    }

    /// <summary>
    /// Loads the row's executable icon.
    ///
    /// Protected processes (csrss.exe, winlogon.exe, ...) cannot have their module
    /// path read, so <see cref="TunerProcessRow.Path"/> is empty and there is
    /// nothing to open. Those still have a real icon on disk - the one Windows
    /// itself shows in Task Manager - so fall back to locating the image by name
    /// in System32 / SysWOW64. This is what keeps locked processes showing their
    /// genuine icon instead of the generic glyph.
    ///
    /// Pseudo-processes with no file at all (System, Registry, ...) get the
    /// wininit.exe icon as the shared fallback, so no row ever shows the
    /// blank-page glyph.
    /// </summary>
    private async Task ExtractIconAsync(TunerProcessRow row)
    {
        _iconTried.Add(row.Pid);
        try
        {
            string path = row.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ResolveSystemImagePath(row.Name) ?? string.Empty;
            }
            Windows.Storage.Streams.IRandomAccessStreamWithContentType? thumb = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var tb = await sf.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
                    if (tb != null && tb.Size > 0)
                        thumb = tb;
                }
                catch { }
            }
            // No file-backed icon (System, Registry, ...): fall back to the
            // wininit.exe bytes - the blue window icon from the PRC list.
            // Copied to a byte array once, so every row decodes from its own
            // in-memory stream: sharing one thumbnail stream across rows raced
            // on Seek and left some rows icon-less.
            byte[]? fallbackBytes = null;
            if (thumb == null)
            {
                fallbackBytes = await GetFallbackIconBytesAsync();
                if (fallbackBytes == null) return;
            }
            var finalThumb = thumb;
            var finalFallbackBytes = fallbackBytes;
            _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (row.AppIcon != null) return;
                    if (finalThumb != null)
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        try { finalThumb.Seek(0); } catch { }
                        await bmp.SetSourceAsync(finalThumb);
                        row.AppIcon = bmp;
                        return;
                    }
                    // Reuse the shared bitmap when another row already decoded it.
                    if (_fallbackAppIcon != null)
                    {
                        row.AppIcon = _fallbackAppIcon;
                        return;
                    }
                    using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    var writer = new Windows.Storage.Streams.DataWriter(mem);
                    writer.WriteBytes(finalFallbackBytes!);
                    await writer.StoreAsync();
                    writer.DetachStream();
                    mem.Seek(0);
                    var fallbackBmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await fallbackBmp.SetSourceAsync(mem);
                    _fallbackAppIcon = fallbackBmp;
                    row.AppIcon = fallbackBmp;
                }
                catch { }
            });
        }
        catch { }
    }

    private byte[]? _fallbackIconBytes;

    /// <summary>Reads the wininit.exe thumbnail into memory once. Every
    /// icon-less row decodes its own copy, so no stream is ever shared.</summary>
    private async Task<byte[]?> GetFallbackIconBytesAsync()
    {
        if (_fallbackIconBytes != null) return _fallbackIconBytes;
        try
        {
            string? path = ResolveSystemImagePath("wininit.exe");
            if (string.IsNullOrWhiteSpace(path)) return null;
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var tb = await sf.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (tb == null || tb.Size == 0) return null;
            var reader = new Windows.Storage.Streams.DataReader(tb);
            await reader.LoadAsync((uint)tb.Size);
            var bytes = new byte[tb.Size];
            reader.ReadBytes(bytes);
            reader.DetachStream();
            _fallbackIconBytes = bytes;
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds a system executable's file by bare name, for processes whose real
    /// path is unreadable. Checks System32 then SysWOW64 (a 32-bit process on
    /// 64-bit Windows lives in the latter).
    /// </summary>
    private static string? ResolveSystemImagePath(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;
        string file = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows)) return null;

        foreach (string dir in new[] { "System32", "SysWOW64" })
        {
            try
            {
                string candidate = System.IO.Path.Combine(windows, dir, file);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private async Task RefreshCpuAsync()
    {
        if (Processes.Count == 0) return;
        
        // Pass the read-only projection directly to the tuning service
        await _tuning.SampleCpuAsync(Processes.ToList());
        _tuning.PruneCache(Processes.Select(p => p.Pid));
    }

    // ─── Thread Tune tab ───────────────────────────────────────────

    private bool IsProcessProtected(string exeName)
    {
        foreach (var p in _profiles.ActiveProfiles)
        {
            if (p.Enabled && p.ProtectThreads)
            {
                string barePattern = (p.Pattern ?? "").Replace("*", "").ToLowerInvariant();
                string exeLower = exeName.ToLowerInvariant();
                if (!string.IsNullOrEmpty(barePattern) && exeLower.Contains(barePattern))
                    return true;
                
                if (string.Equals(p.Name, exeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

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

                    bool procProtected = IsProcessProtected(procName);

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
                                isProtected = procProtected;
                            }
                        }
                        catch { }

                        try
                        {
                            desc = NativeSnapshotService.TryGetThreadDescription((uint)tid) ?? "(unnamed)";

                            // Visual enforcement: if the user made an explicit
                            // choice for this thread, show THAT rather than
                            // whatever the process-wide default last wrote to
                            // it. Both directions are honoured - a thread the
                            // user switched back on must not read as "off"
                            // just because the process-level preference turned
                            // it off.
                            bool? wanted = BoostPreferenceService.Instance.GetPreference(
                                procName, tid, desc, string.Empty);
                            if (wanted.HasValue)
                            {
                                boost = wanted.Value;
                            }
                        }
                        catch { }

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
            catch
            {
                return rows;
            }
        });

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
                    // Patch in place - don't touch BoostEnabled if user is toggling
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

        var visible = new List<ThreadBoostRow>();
        foreach (var r in ThreadBoostRows)
        {
            if (HideUnnamedThreads && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase))
                continue;

            if (filter.Length == 0
                || r.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Tid.ToString().Contains(filter, StringComparison.Ordinal)
                || r.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(r);
            }
        }

        // Identical set+order (the common poll case): leave the collection
        // alone - Clear/Add resets the ListView scroll position and the
        // user's mouse wheel position every tick.
        if (DisplayedThreadBoostRows.Count == visible.Count)
        {
            bool same = true;
            for (int i = 0; i < visible.Count; i++)
            {
                if (!ReferenceEquals(DisplayedThreadBoostRows[i], visible[i])) { same = false; break; }
            }
            if (same) return;
        }

        // Minimal in-place edit: remove only rows that left the view and
        // insert only rows that joined it, at their correct positions. A
        // Clear()+Add() rebuild fires a full items-reset that snaps the
        // ListView back to the top every time any thread appears or dies.
        var desired = new HashSet<ThreadBoostRow>(visible);
        for (int i = DisplayedThreadBoostRows.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(DisplayedThreadBoostRows[i]))
                DisplayedThreadBoostRows.RemoveAt(i);
        }

        var position = new Dictionary<ThreadBoostRow, int>();
        for (int i = 0; i < visible.Count; i++) position[visible[i]] = i;

        foreach (var r in visible)
        {
            if (DisplayedThreadBoostRows.Contains(r)) continue;
            int insertAt = DisplayedThreadBoostRows.Count;
            for (int i = 0; i < DisplayedThreadBoostRows.Count; i++)
            {
                if (position[DisplayedThreadBoostRows[i]] > position[r]) { insertAt = i; break; }
            }
            DisplayedThreadBoostRows.Insert(insertAt, r);
        }
    }
}
