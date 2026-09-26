// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly UninstallService _uninstallService = new();
    private readonly LeftoverScannerService _scannerService = new();
    private readonly InstallMonitorService _monitorService = new();
    private readonly DriverStoreService _driverService = new();
    private readonly StartupManagerService _startupService = new();

    public ObservableCollection<UninstallerItem> Apps { get; } = new();

    // ---- Driver packages section (driver store, oem##.inf) ----

    public ObservableCollection<DriverPackageItem> DriverPackages { get; } = new();
    public ObservableCollection<DriverPackageItem> FilteredDrivers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDriversEmpty))]
    public partial bool IsLoadingDrivers { get; set; }
    [ObservableProperty]
    public partial string DriverSearchQuery { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string DriverSummaryText { get; set; } = "Driver packages not scanned yet.";
    [ObservableProperty]
    public partial string DriverActionStatus { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDriversEmpty))]
    public partial bool HasDriverResults { get; set; }
    public bool ShowDriversEmpty => !IsLoadingDrivers && !HasDriverResults;
    [ObservableProperty]
    public partial DriverPackageItem? SelectedDriver { get; set; }

    partial void OnDriverSearchQueryChanged(string value) => UpdateDriverFilter();

    private void UpdateDriverFilter()
    {
        IEnumerable<DriverPackageItem> q = DriverPackages;
        if (!string.IsNullOrWhiteSpace(DriverSearchQuery))
            q = q.Where(d => d.DisplayName.Contains(DriverSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             d.Provider.Contains(DriverSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             d.ClassName.Contains(DriverSearchQuery, StringComparison.OrdinalIgnoreCase) ||
                             d.PublishedName.Contains(DriverSearchQuery, StringComparison.OrdinalIgnoreCase));
        var list = q.OrderBy(d => d.Provider).ThenBy(d => d.DisplayName).ToList();
        FilteredDrivers.Clear();
        foreach (var d in list) FilteredDrivers.Add(d);
        long totalBytes = 0;
        foreach (var d in DriverPackages) if (d.StoreSizeBytes > 0) totalBytes += d.StoreSizeBytes;
        string total = totalBytes switch { < 1024 => $"{totalBytes:F0} B", < 1024 * 1024 => $"{totalBytes / 1024:F1} KB", < 1024 * 1024 * 1024 => $"{totalBytes / 1024 / 1024:F1} MB", _ => $"{totalBytes / 1024 / 1024 / 1024:F1} GB" };
        DriverSummaryText = $"{FilteredDrivers.Count} shown • {DriverPackages.Count} third-party package(s) • {total} in store";
        HasDriverResults = FilteredDrivers.Count > 0;
    }

    [RelayCommand]
    private async Task LoadDriversAsync()
    {
        if (IsLoadingDrivers) return;
        IsLoadingDrivers = true;
        DriverActionStatus = string.Empty;
        try
        {
            var progress = new Progress<string>(s => DriverSummaryText = s);
            var items = await _driverService.GetPackagesAsync(CancellationToken.None, progress);
            DriverPackages.Clear();
            foreach (var item in items) DriverPackages.Add(item);
            UpdateDriverFilter();
        }
        catch { DriverSummaryText = "Driver scan failed."; }
        finally { IsLoadingDrivers = false; }
    }

    [RelayCommand]
    private async Task RemoveDriverAsync()
    {
        var targets = DriverPackages.Where(d => d.IsSelected).ToList();
        if (SelectedDriver != null && targets.Count == 0) targets.Add(SelectedDriver);
        if (targets.Count == 0) { DriverActionStatus = "Nothing selected."; return; }
        IsLoadingDrivers = true;
        try
        {
            int ok = 0;
            var errors = new List<string>();
            foreach (var driver in targets)
            {
                try
                {
                    DriverSummaryText = $"Removing {driver.PublishedName}…";
                    var err = await Task.Run(() => _driverService.DeletePackageAsync(driver).GetAwaiter().GetResult());
                    if (err != null) { errors.Add($"{driver.PublishedName}: {err}"); continue; }
                    ok++;
                }
                catch (Exception ex) { errors.Add($"{driver.PublishedName}: {ex.Message}"); }
            }
            IsLoadingDrivers = false; // release first: reload guards on it
            await LoadDriversAsync();
            DriverActionStatus = errors.Count == 0
                ? $"Removed {ok} driver package{(ok == 1 ? "" : "s")}. A restart may be needed for devices using them."
                : $"{ok} removed, {errors.Count} failed: " + string.Join("; ", errors.Take(3));
        }
        finally { IsLoadingDrivers = false; }
    }

    // ---- Startup tab (Run values, services, scheduled tasks) ----

    public ObservableCollection<StartupEntry> StartupHkcu { get; } = new();
    public ObservableCollection<StartupEntry> FilteredHkcu { get; } = new();
    public ObservableCollection<StartupEntry> StartupHklm { get; } = new();
    public ObservableCollection<StartupEntry> FilteredHklm { get; } = new();
    public ObservableCollection<StartupEntry> StartupServices { get; } = new();
    public ObservableCollection<StartupEntry> FilteredServices { get; } = new();
    public ObservableCollection<StartupEntry> StartupTasks { get; } = new();
    public ObservableCollection<StartupEntry> FilteredTasks { get; } = new();

    /// <summary>The side panel's sources. One list is shown at a time; the counts are
    /// post-filter so the panel agrees with what the list shows.</summary>
    public ObservableCollection<StartupSectionVm> StartupSections { get; } = new()
    {
        new StartupSectionVm("HKCU Run",
            "Applications Windows launches for this user. Unchecking keeps the command in kaliteConfig's backup store, so it can be switched back on."),
        new StartupSectionVm("HKLM Run",
            "Machine-wide startup values - they run for every account on this PC."),
        new StartupSectionVm("Services",
            "User-mode services only (own process, shared process, interactive). Checked = automatic start, unchecked = start mode Disabled."),
        new StartupSectionVm("Scheduled Tasks",
            "Non-Microsoft scheduled tasks. Unchecked = task disabled; the trash button deletes the task itself."),
    };

    [ObservableProperty]
    public partial int StartupSectionIndex { get; set; }

    public StartupSectionVm CurrentStartupSection =>
        StartupSections[Math.Clamp(StartupSectionIndex, 0, StartupSections.Count - 1)];

    /// <summary>The list for the selected source - never all four at once.</summary>
    public ObservableCollection<StartupEntry> CurrentStartupEntries => StartupSectionIndex switch
    {
        0 => FilteredHkcu,
        1 => FilteredHklm,
        2 => FilteredServices,
        _ => FilteredTasks,
    };

    /// <summary>Only the selected source's list is on screen.</summary>
    public Microsoft.UI.Xaml.Visibility HkcuVis => SourceVis(0);
    public Microsoft.UI.Xaml.Visibility HklmVis => SourceVis(1);
    public Microsoft.UI.Xaml.Visibility ServicesVis => SourceVis(2);
    public Microsoft.UI.Xaml.Visibility TasksVis => SourceVis(3);

    private Microsoft.UI.Xaml.Visibility SourceVis(int index) => StartupSectionIndex == index
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The "hide Microsoft services" switch describes services only.</summary>
    public Microsoft.UI.Xaml.Visibility MicrosoftFilterVis => SourceVis(2);

    public Microsoft.UI.Xaml.Visibility EmptyVis => CurrentStartupEntries.Count == 0
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The Type column means something different per source; Run values have none.</summary>
    public string TypeHeaderText => StartupSectionIndex switch
    {
        2 => "Start mode",
        3 => "Trigger",
        _ => "",
    };

    partial void OnStartupSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentStartupSection));
        OnPropertyChanged(nameof(CurrentStartupEntries));
        OnPropertyChanged(nameof(HkcuVis));
        OnPropertyChanged(nameof(HklmVis));
        OnPropertyChanged(nameof(ServicesVis));
        OnPropertyChanged(nameof(TasksVis));
        OnPropertyChanged(nameof(MicrosoftFilterVis));
        OnPropertyChanged(nameof(EmptyVis));
        OnPropertyChanged(nameof(TypeHeaderText));
    }

    [ObservableProperty]
    public partial string StartupSearchQuery { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool HideMicrosoftServices { get; set; } = true;
    [ObservableProperty]
    public partial string StartupSummaryText { get; set; } = "Startup entries not scanned yet.";
    [ObservableProperty]
    public partial string StartupStatusText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsLoadingStartup { get; set; }
    [ObservableProperty]
    public partial bool StartupLoaded { get; set; }

    partial void OnStartupSearchQueryChanged(string value) => UpdateStartupFilter();
    partial void OnHideMicrosoftServicesChanged(bool value) => UpdateStartupFilter();

    private bool _suppressStartupToggle;

    private void UpdateStartupFilter()
    {
        FilterStartupList(StartupHkcu, FilteredHkcu);
        FilterStartupList(StartupHklm, FilteredHklm);
        FilterStartupList(StartupServices, FilteredServices,
            e => !HideMicrosoftServices || !StartupManagerService.IsMicrosoftCompany(e.Company));
        FilterStartupList(StartupTasks, FilteredTasks);
        int total = StartupHkcu.Count + StartupHklm.Count + StartupServices.Count + StartupTasks.Count;
        int shown = FilteredHkcu.Count + FilteredHklm.Count + FilteredServices.Count + FilteredTasks.Count;
        StartupSummaryText = $"{shown} shown • {total} startup entries";
        var counts = new (int Shown, int Total)[]
        {
            (FilteredHkcu.Count, StartupHkcu.Count),
            (FilteredHklm.Count, StartupHklm.Count),
            (FilteredServices.Count, StartupServices.Count),
            (FilteredTasks.Count, StartupTasks.Count),
        };
        for (int i = 0; i < StartupSections.Count && i < counts.Length; i++)
        {
            StartupSections[i].Shown = counts[i].Shown;
            StartupSections[i].Total = counts[i].Total;
        }
        OnPropertyChanged(nameof(EmptyVis));
    }

    private void FilterStartupList(
        ObservableCollection<StartupEntry> source,
        ObservableCollection<StartupEntry> target,
        Func<StartupEntry, bool>? extra = null)
    {
        target.Clear();
        foreach (var e in source)
        {
            if (extra != null && !extra(e)) continue;
            if (!string.IsNullOrWhiteSpace(StartupSearchQuery)
                && !e.Name.Contains(StartupSearchQuery, StringComparison.OrdinalIgnoreCase)
                && !e.Command.Contains(StartupSearchQuery, StringComparison.OrdinalIgnoreCase)
                && !e.Id.Contains(StartupSearchQuery, StringComparison.OrdinalIgnoreCase))
                continue;
            target.Add(e);
        }
    }

    [RelayCommand]
    private async Task LoadStartupAsync()
    {
        if (IsLoadingStartup) return;
        IsLoadingStartup = true;
        StartupStatusText = string.Empty;
        try
        {
            var progress = new Progress<string>(s => StartupSummaryText = s);
            var scan = await _startupService.ScanAsync(CancellationToken.None, progress);
            FillStartupList(StartupHkcu, scan.UserRun);
            FillStartupList(StartupHklm, scan.MachineRun);
            FillStartupList(StartupServices, scan.Services);
            FillStartupList(StartupTasks, scan.Tasks);
            UpdateStartupFilter();
            StartupLoaded = true;
        }
        catch { StartupSummaryText = "Startup scan failed."; }
        finally { IsLoadingStartup = false; }
    }

    private void FillStartupList(ObservableCollection<StartupEntry> target, List<StartupEntry> items)
    {
        foreach (var e in target) e.PropertyChanged -= OnStartupEntryToggled;
        target.Clear();
        foreach (var e in items) { e.PropertyChanged += OnStartupEntryToggled; target.Add(e); }
    }

    /// <summary>
    /// Commits checkbox flips through the service (same pattern as the
    /// kernel-tweak toggles in PowerPlansViewModel). Reverts the checkbox
    /// when the write fails so the UI never lies about the applied state.
    /// </summary>
    private async void OnStartupEntryToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressStartupToggle || e.PropertyName != nameof(StartupEntry.IsEnabled)) return;
        if (sender is not StartupEntry entry) return;
        bool want = entry.IsEnabled;
        string? err = await _startupService.SetEnabledAsync(entry, want);
        if (err is null)
        {
            if (entry.Kind == StartupEntryKind.Service)
                entry.EntryType = want ? "Auto" : "Disabled";
            StartupStatusText = $"{entry.Name} {(want ? "enabled" : "disabled")}.";
        }
        else
        {
            _suppressStartupToggle = true;
            try { entry.IsEnabled = !want; }
            finally { _suppressStartupToggle = false; }
            StartupStatusText = $"Couldn't change {entry.Name}: {err}";
        }
    }

    [RelayCommand]
    private async Task DeleteStartupEntryAsync(StartupEntry? entry)
    {
        if (entry is null) return;
        string? err = await _startupService.DeleteAsync(entry);
        if (err is null)
        {
            foreach (var master in new[] { StartupHkcu, StartupHklm, StartupServices, StartupTasks })
                if (master.Contains(entry)) { master.Remove(entry); break; }
            UpdateStartupFilter();
            StartupStatusText = $"Removed {entry.Name}.";
        }
        else
        {
            StartupStatusText = $"Couldn't remove {entry.Name}: {err}";
        }
    }

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
    /// <summary>
    /// True while the Uninstaller tab is active. The search/sort/export
    /// toolbar describes applications only, so it hides on the other tabs.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUninstallerTabActive { get; set; } = true;
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
        // Newly visible rows may still lack icons - loader is re-entrancy guarded.
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

            // Refresh list (LoadAppsAsync guards on IsLoading - release first)
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
            IsLoading = false; // LoadAppsAsync guards on IsLoading - release first
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

/// <summary>
/// One source in the Startup tab's side panel: its title, what the source means,
/// and how many of its entries survive the current search / hide filter.
/// </summary>
public sealed partial class StartupSectionVm : ObservableObject
{
    public string Title { get; }
    public string Hint { get; }

    public StartupSectionVm(string title, string hint)
    {
        Title = title;
        Hint = hint;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Shown { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Total { get; set; }

    public string CountText => Shown == Total ? Total.ToString() : $"{Shown} of {Total}";
}
