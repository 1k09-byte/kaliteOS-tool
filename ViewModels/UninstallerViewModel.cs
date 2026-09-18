using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly UninstallService _uninstallService = new();
    private readonly LeftoverScannerService _scannerService = new();
    private readonly InstallMonitorService _monitorService = new();

    public ObservableCollection<UninstallerItem> Apps { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; set; }
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty]
    public partial int SortIndex { get; set; }
    [ObservableProperty]
    public partial int FilterIndex { get; set; }
    [ObservableProperty]
    public partial bool ShowSystem { get; set; }
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "Loading…";
    [ObservableProperty]
    public partial string InfoText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string LoadingStatus { get; set; } = "Starting…";
    [ObservableProperty]
    public partial string ActionStatus { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool HasResults { get; set; }
    public bool ShowEmpty => !IsLoading && !HasResults;
    [ObservableProperty]
    public partial UninstallerItem? SelectedApp { get; set; }

    public ObservableCollection<UninstallerItem> FilteredApps { get; } = new();

    partial void OnSearchQueryChanged(string value) => UpdateFilter();
    partial void OnSortIndexChanged(int value) => UpdateFilter();
    partial void OnFilterIndexChanged(int value) => UpdateFilter();
    partial void OnShowSystemChanged(bool value) => UpdateFilter();

    private void UpdateFilter()
    {
        IEnumerable<UninstallerItem> q = Apps;
        if (!ShowSystem) q = q.Where(a => !a.IsSystemComponent);
        if (FilterIndex == 1) q = q.Where(a => a.InstallType == InstallType.Registry || a.InstallType == InstallType.Msi);
        else if (FilterIndex == 2) q = q.Where(a => a.InstallType == InstallType.AppX);
        else if (FilterIndex == 3) q = q.Where(a => !a.HasUninstaller);
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            q = q.Where(a => a.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             a.Publisher.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
        q = SortIndex switch
        {
            1 => q.OrderBy(a => a.Publisher).ThenBy(a => a.Name),
            2 => q.OrderByDescending(a => a.EffectiveSize),
            3 => q.OrderByDescending(a => a.InstallDate),
            _ => q.OrderBy(a => a.Name),
        };
        FilteredApps.Clear();
        foreach (var app in q) FilteredApps.Add(app);
        var totalBytes = Apps.Sum(a => (double)a.EffectiveSize);
        string total = totalBytes switch { < 1024 => $"{totalBytes:F0} B", < 1024*1024 => $"{totalBytes/1024:F1} KB", < 1024*1024*1024 => $"{totalBytes/1024/1024:F1} MB", _ => $"{totalBytes/1024/1024/1024:F1} GB" };
        SummaryText = $"{FilteredApps.Count} shown • {Apps.Count} installed • {total} registered";
        InfoText = $"{Apps.Count} applications found.";
        HasResults = FilteredApps.Count > 0;
        // Newly visible rows may still lack icons — loader is re-entrancy guarded.
        _ = LoadIconsAsync();
    }

    // Monitor State
    [ObservableProperty]
    public partial bool IsMonitoring { get; set; }
    private bool _iconsLoading;
    private bool _iconsDirty;
    [ObservableProperty]
    public partial int MonitorProgress { get; set; }
    [ObservableProperty]
    public partial string MonitorStatus { get; set; } = "Ready to create installation snapshot.";
    private InstallMonitorSnapshot? _beforeSnapshot;
    [ObservableProperty]
    public partial InstallMonitorDiff? LatestSnapshotDiff { get; set; }

    public UninstallerViewModel()
    {
    }

    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        LoadingStatus = "Starting scan…";

        try
        {
            var progress = new Progress<string>(s => LoadingStatus = s);
            var apps = await _uninstallService.GetAllAppsAsync(CancellationToken.None, progress);
            Apps.Clear();
            foreach (var app in apps.OrderBy(a => a.Name))
            {
                Apps.Add(app);
            }
            UpdateFilter();
            LoadingStatus = "Measuring install folders…";
            // Real per-app icons (UI thread, small batches so the list stays fluid).
            _ = LoadIconsAsync();
            // Background real-size enrichment; applied on the UI thread so
            // PropertyChanged always fires where the bindings live.
            LoadingStatus = "Measuring install folders…";
            _ = _uninstallService.ComputeRealSizesAsync(apps, CancellationToken.None)
                .ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsCompletedSuccessfully && t.Result.Count > 0)
                        {
                            var byId = Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in t.Result)
                                if (byId.TryGetValue(kv.Key, out var item))
                                    item.ComputedSize = kv.Value;
                            UpdateFilter();
                        }
                    }
                    catch { }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch { }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var targetApps = Apps.Where(a => a.IsSelected).ToList();
        if (SelectedApp != null && targetApps.Count == 0) targetApps.Add(SelectedApp);
        if (targetApps.Count == 0) { ActionStatus = "Nothing selected."; return; }

        IsLoading = true;
        try
        {
            int ok = 0;
            var errors = new System.Collections.Generic.List<string>();
            foreach (var app in targetApps)
            {
                try
                {
                    LoadingStatus = $"Uninstalling {app.Name}…";
                    // Run standard uninstall (returns an error string, null = launched OK)
                    var err = await Task.Run(() => _uninstallService.Uninstall(app));
                    if (err != null) { errors.Add($"{app.Name}: {err}"); continue; }
                    ok++;

                    // Ask scanner for leftovers
                    var leftovers = await _scannerService.ScanLeftoversAsync(app);
                    if (leftovers.Count > 0)
                    {
                        await _scannerService.CleanLeftoversAsync(leftovers);
                    }
                }
                catch (Exception ex) { errors.Add($"{app.Name}: {ex.Message}"); }
            }

            // Refresh list (LoadAppsAsync guards on IsLoading — release first)
            IsLoading = false;
            await LoadAppsAsync();
            ActionStatus = errors.Count == 0
                ? $"Uninstalled {ok} application{(ok == 1 ? "" : "s")}."
                : $"{ok} uninstalled, {errors.Count} failed: " + string.Join("; ", errors.Take(3));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadIconsAsync()
    {
        if (_iconsLoading) { _iconsDirty = true; return; }
        _iconsLoading = true;
        try
        {
            do
            {
                _iconsDirty = false;
                // Filtered order = what the user actually sees; cap keeps startup fast.
                var pending = FilteredApps.Take(400).Where(a => a.Icon == null).ToList();
                using var gate = new SemaphoreSlim(8);
                var tasks = pending.Select(async app =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var candidate = AppIconService.ResolveCandidate(app);
                        if (candidate == null) return;
                        var bmp = await AppIconService.TryGetIconAsync(candidate);
                        if (bmp != null) app.Icon = bmp;
                    }
                    catch { }
                    finally { gate.Release(); }
                });
                await Task.WhenAll(tasks);
                int withIcons = FilteredApps.Count(a => a.Icon != null);
                LoadingStatus = $"Loading icons… ({withIcons}/{FilteredApps.Count})";
            } while (_iconsDirty);
        }
        catch { }
        finally { _iconsLoading = false; }
    }

    [RelayCommand]
    private void OpenInstallFolder()
    {
        var loc = SelectedApp?.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(loc)) return;
        try
        {
            if (System.IO.Directory.Exists(loc))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{loc}\"") { UseShellExecute = true });
            else if (System.IO.File.Exists(loc))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{loc}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task ForceRemoveAsync()
    {
        var targets = Apps.Where(a => a.IsSelected).ToList();
        if (SelectedApp != null && targets.Count == 0) targets.Add(SelectedApp);
        if (targets.Count == 0) { ActionStatus = "Nothing selected."; return; }
        IsLoading = true;
        try
        {
            int cleaned = 0;
            var errors = new System.Collections.Generic.List<string>();
            foreach (var app in targets)
            {
                try
                {
                    LoadingStatus = $"Force-removing {app.Name}…";
                    var notes = await Task.Run(() =>
                    {
                        var n = new List<string>();
                        // 1. Delete the install directory if it still exists.
                        if (_uninstallService.RemoveInstallDirectory(app))
                            n.Add("install folder deleted");
                        // 2. Scan & clean leftover files/registry matches.
                        var leftovers = _scannerService.ScanLeftoversAsync(app).GetAwaiter().GetResult();
                        if (leftovers.Count > 0)
                        {
                            _scannerService.CleanLeftoversAsync(leftovers).GetAwaiter().GetResult();
                            n.Add($"{leftovers.Count} leftover path(s) cleaned");
                        }
                        // 3. Remove the uninstall registry entry so the app
                        //    disappears from this list and Windows' own list.
                        int reg = _uninstallService.RemoveRegistryEntry(app);
                        if (reg > 0) n.Add("uninstall entry removed");
                        return n;
                    });
                    cleaned++;
                    if (notes.Count > 0)
                        errors.Add($"{app.Name}: " + string.Join(", ", notes));
                }
                catch (Exception ex) { errors.Add($"{app.Name}: {ex.Message}"); }
            }
            IsLoading = false; // LoadAppsAsync guards on IsLoading — release first
            await LoadAppsAsync();
            ActionStatus = errors.Count == 0
                ? $"Force-removed residue for {cleaned} application{(cleaned == 1 ? "" : "s")}."
                : string.Join("; ", errors.Take(3));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ExportListAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (App.MainWindow != null)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeChoices.Add("CSV", new[] { ".csv" });
            picker.SuggestedFileName = "installed-apps.csv";
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            var lines = new System.Collections.Generic.List<string> { "Name,Publisher,Version,Size,Type,Uninstall" };
            foreach (var a in Apps)
                lines.Add($"\"{a.Name}\",\"{a.Publisher}\",\"{a.Version}\",\"{a.FormattedSize}\",\"{a.InstallType}\",\"{a.UninstallString}\"");
            await Windows.Storage.FileIO.WriteLinesAsync(file, lines);
        }
        catch { }
    }

    [RelayCommand]
    private async Task StartMonitorSnapshotAsync()
    {
        if (IsMonitoring) return;
        IsMonitoring = true;
        MonitorProgress = 0;
        MonitorStatus = "Creating \"Before\" Snapshot... (This may take a minute)";

        try
        {
            var progress = new Progress<int>(p => MonitorProgress = p);
            _beforeSnapshot = await _monitorService.CreateSnapshotAsync(progress, CancellationToken.None);
            MonitorStatus = "Snapshot captured. Install your software, then click 'Compare Snapshot'.";
        }
        catch (Exception ex)
        {
            MonitorStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsMonitoring = false;
        }
    }

    [RelayCommand]
    private async Task CompareMonitorSnapshotAsync()
    {
        if (IsMonitoring || _beforeSnapshot == null) return;
        IsMonitoring = true;
        MonitorProgress = 0;
        MonitorStatus = "Creating \"After\" Snapshot & Computing Diff...";

        try
        {
            var progress = new Progress<int>(p => MonitorProgress = p);
            var afterSnapshot = await _monitorService.CreateSnapshotAsync(progress, CancellationToken.None);
            
            LatestSnapshotDiff = await _monitorService.CompareSnapshotsAsync(_beforeSnapshot, afterSnapshot, CancellationToken.None);
            
            MonitorStatus = $"Diff Complete: {LatestSnapshotDiff.AddedFiles.Count} files, {LatestSnapshotDiff.AddedRegistryKeys.Count} registry keys.";
            _beforeSnapshot = null; // Reset
        }
        catch (Exception ex)
        {
            MonitorStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsMonitoring = false;
        }
    }
}
