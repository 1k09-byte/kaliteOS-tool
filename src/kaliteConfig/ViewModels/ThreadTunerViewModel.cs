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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessListLoadingVis))]
    public partial bool IsProcessListLoading { get; set; } = true;

    public Microsoft.UI.Xaml.Visibility IsProcessListLoadingVis =>
        IsProcessListLoading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private readonly HashSet<int> _iconTried = new();

    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _fallbackAppIcon;

    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "threadtuner-settings.json");

    private class ThreadTunerSettings
    {
        public bool ShowCounterDeltas { get; set; }
    }

    public ObservableCollection<TunerProcessRow> Processes { get; } = new();
    public ObservableCollection<TunerProcessRow> DisplayedProcesses { get; } = new();
    public ObservableCollection<TunerProfile> Profiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProfiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedProcessProfiles { get; } = new();
    public ObservableCollection<TunerProfile> DisplayedThreadProfiles { get; } = new();

    public ObservableCollection<ThreadBoostRow> ThreadBoostRows { get; } = new();
    public ObservableCollection<ThreadBoostRow> DisplayedThreadBoostRows { get; } = new();
    [ObservableProperty]
    public partial bool IsThreadTuneTabActive { get; set; }

    private void Profiles_RulesChanged(object? sender, EventArgs e) => SyncProfiles();

    public void Shutdown()
    {
        try { _timer.Stop(); } catch { }
        try { _searchTimer?.Stop(); } catch { }
        _searchTimer = null;
        try { _profiles.RulesChanged -= Profiles_RulesChanged; } catch { }
        try { Optimizer.Shutdown(); } catch { }
    }

    public bool IsPageVisible { get; set; } = true;

    public bool IsWindowActive { get; set; } = true;

    private bool ShouldPoll => IsPageVisible && IsWindowActive;

    [ObservableProperty]
    public partial bool IsThreadTuneLoading { get; set; }

    [ObservableProperty]
    public partial string ThreadTuneLoadingText { get; set; } = "Scanning system threads...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCounterDeltasVis))]
    public partial bool ShowCounterDeltas { get; set; }

    public Microsoft.UI.Xaml.Visibility ShowCounterDeltasVis =>
        ShowCounterDeltas ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnShowCounterDeltasChanged(bool value)
    {
        _tuning.CollectCounters = value;
        if (value) _tuning.ResetCounterBaselines();
        RefreshDisplayedProcesses();
        _ = SaveSettingsAsync();
    }

    private readonly HashSet<int> _collapsed = new();

    public bool HideUnnamedThreads => true;

    public Task AutoDisableUnnamedBoostsAsync()
    {
        return Task.Run(async () =>
        {
            var rowsToDisable = ThreadBoostRows.Where(r => !r.IsProtected && r.IsBoostKnown && r.BoostEnabled && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in rowsToDisable)
            {
                try
                {
                    await App.Current.ThreadTuning.SetBoostAsync((uint)r.Tid, enabled: false);
                    _dispatcher.TryEnqueue(() => r.BoostEnabled = false);
                }
                catch { }
            }
        });
    }

    public Task RestoreUnnamedBoostsAsync()
    {
        return Task.Run(async () =>
        {
            var rowsToRestore = ThreadBoostRows.Where(r => !r.IsProtected && r.IsBoostKnown && !r.BoostEnabled && string.Equals(r.Description, "(unnamed)", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in rowsToRestore)
            {
                try
                {
                    await App.Current.ThreadTuning.SetBoostAsync((uint)r.Tid, enabled: true);
                    _dispatcher.TryEnqueue(() => r.BoostEnabled = true);
                }
                catch { }
            }
        });
    }

    [ObservableProperty]
    public partial TunerProcessRow? SelectedProcess { get; set; }

    [ObservableProperty]
    public partial TunerProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string RulesFilter { get; set; } = string.Empty;

    public ThreadTunerViewModel()
    {
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
                if (_refreshing)
                {
                    return;
                }

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

        foreach (var p in _profiles.ActiveProfiles)
        {
            Profiles.Add(p);
        }
        RefreshDisplayedProfiles();

        _profiles.RulesChanged += Profiles_RulesChanged;
        
        
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

    private List<TunerProcessRow> OrderAsTree(IEnumerable<TunerProcessRow> all)
    {
        var byPid = new Dictionary<int, TunerProcessRow>();
        var children = new Dictionary<int, List<TunerProcessRow>>();
        var roots = new List<TunerProcessRow>();

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
                    Processes.Add(p);
                    changed = true;
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
                    if (existing.AppIcon == null && _iconTried.Add(existing.Pid))
                        _ = ExtractIconAsync(existing);
                }
            }

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

            if (IsProcessListLoading && Processes.Count > 0)
            {
                IsProcessListLoading = false;
            }
        });
    }

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

        await _tuning.SampleCpuAsync(Processes.ToList());
        _tuning.PruneCache(Processes.Select(p => p.Pid));
    }

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

                        bool boost = false;
                        bool boostKnown = false;
                        bool isProtected = true;
                        string desc = "(unnamed)";
                        
                        try
                        {
                            using var threadHandle = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.QueryInformation, false, (uint)tid);
                            if (!threadHandle.IsInvalid && NativeMethods.Priority.GetThreadPriorityBoost(threadHandle, out bool disabled))
                            {
                                boost = !disabled;
                                boostKnown = true;
                                isProtected = procProtected;
                            }
                        }
                        catch { }

                        try
                        {
                            desc = NativeSnapshotService.TryGetThreadDescription((uint)tid) ?? "(unnamed)";

                            bool? wanted = BoostPreferenceService.Instance.GetPreference(
                                procName, tid, desc, string.Empty);
                            if (wanted.HasValue)
                            {
                                boost = wanted.Value;
                                boostKnown = true;
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
                            IsBoostKnown = boostKnown,
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

            for (int i = ThreadBoostRows.Count - 1; i >= 0; i--)
            {
                if (!liveTids.Contains(ThreadBoostRows[i].Tid))
                    ThreadBoostRows.RemoveAt(i);
            }

            var existingByTid = new Dictionary<int, ThreadBoostRow>();
            foreach (var r in ThreadBoostRows) existingByTid[r.Tid] = r;

            foreach (var r in allRows)
            {
                if (existingByTid.TryGetValue(r.Tid, out var existing))
                {
                    if (!existing.IsBusy && existing.BoostEnabled != r.BoostEnabled)
                        existing.BoostEnabled = r.BoostEnabled;
                    if (!existing.IsBusy && existing.IsBoostKnown != r.IsBoostKnown)
                        existing.IsBoostKnown = r.IsBoostKnown;
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

        if (DisplayedThreadBoostRows.Count == visible.Count)
        {
            bool same = true;
            for (int i = 0; i < visible.Count; i++)
            {
                if (!ReferenceEquals(DisplayedThreadBoostRows[i], visible[i])) { same = false; break; }
            }
            if (same) return;
        }

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
