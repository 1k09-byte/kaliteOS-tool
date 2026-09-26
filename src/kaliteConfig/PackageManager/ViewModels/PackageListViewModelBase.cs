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
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>Density of the package list (the three bottom-right buttons).</summary>
public enum PackageViewMode
{
    List,
    Compact,
    Grid,
}

/// <summary>Sort keys for the Order-by dropdown.</summary>
public enum PackageSortKey
{
    Name,
    Source,
    Version,
}

/// <summary>One checkbox row in the Sources panel.</summary>
public sealed partial class SourceChoice : ObservableObject
{
    [ObservableProperty] public partial string SourceId { get; set; } = string.Empty;
    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsChecked { get; set; } = true;
}

/// <summary>
/// Shared list behavior for Discover/Updates/Installed: items, filtering,
/// sorting, selection + counts, view mode, sources panel. Each view fills
/// Items and tweaks the chrome; the row control renders everything.
/// </summary>
public abstract partial class PackageListViewModelBase : ObservableObject
{
    protected readonly PackageManagerModule Module = PackageManagerModule.Instance;

    /// <summary>
    /// UI-thread dispatcher captured at construction (ViewModels are always
    /// built on the UI thread). Never use GetForCurrentThread() at await
    /// points: after ConfigureAwait(false) hops, "current" is a pool thread
    /// and any UI update from there dies with RPC_E_WRONG_THREAD.
    /// </summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    protected PackageListViewModelBase()
    {
        try { _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { _dispatcher = null; }
    }

    public ObservableCollection<PackageInfo> Items { get; } = new();
    public ObservableCollection<PackageInfo> Filtered { get; } = new();
    public ObservableCollection<SourceChoice> SourceChoices { get; } = new();

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial PackageViewMode ViewMode { get; set; } = PackageViewMode.List;
    [ObservableProperty] public partial PackageSortKey SortKey { get; set; } = PackageSortKey.Name;
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>ComboBox-friendly mirror of SortKey (same declaration order).</summary>
    public int SortKeyIndex
    {
        get => (int)SortKey;
        set { SortKey = (PackageSortKey)value; OnPropertyChanged(); }
    }
    [ObservableProperty] public partial string StatusLine { get; set; } = string.Empty;
    [ObservableProperty] public partial string CountsLine { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime? LastCheckedAt { get; set; }

    public int SelectedCount => Filtered.Count(p => p.IsSelected);

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSortKeyChanged(PackageSortKey value) => ApplyFilter();

    /// <summary>Which sources participate (checked ones); views override the capability filter.</summary>
    protected virtual bool SourceEligible(IPackageSource source) => true;

    protected void RebuildSourceChoices()
    {
        var keep = SourceChoices.Where(c => c.IsChecked).Select(c => c.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool first = SourceChoices.Count == 0;
        SourceChoices.Clear();
        foreach (var source in Module.Sources.Sources)
        {
            if (!Module.Sources.IsEnabled(source) || !SourceEligible(source)) continue;
            SourceChoices.Add(new SourceChoice
            {
                SourceId = source.SourceId,
                Label = source.DisplayName,
                IsChecked = first || keep.Contains(source.SourceId),
            });
        }
        foreach (var choice in SourceChoices)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SourceChoice.IsChecked)) ApplyFilter();
            };
    }

    protected HashSet<string> CheckedSourceIds() => SourceChoices
        .Where(c => c.IsChecked).Select(c => c.SourceId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    protected void ApplyFilter()
    {
        var allowed = CheckedSourceIds();
        IEnumerable<PackageInfo> q = Items.Where(p => allowed.Contains(p.SourceId));
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string query = SearchQuery.Trim();
            q = q.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        q = SortKey switch
        {
            PackageSortKey.Source => q.OrderBy(p => p.SourceLabel).ThenBy(p => p.Name),
            PackageSortKey.Version => q.OrderByDescending(p => p.AvailableVersion).ThenBy(p => p.Name),
            _ => q.OrderBy(p => p.Name),
        };
        Filtered.Clear();
        foreach (var p in q) Filtered.Add(p);
        RefreshCounts();
        OnPropertyChanged(nameof(SelectedCount));
    }

    protected virtual void RefreshCounts()
    {
        CountsLine = $"{Filtered.Count} shown • {Items.Count} loaded • {SelectedCount} selected";
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var p in Filtered) p.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var p in Filtered) p.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    public void CheckAllSources()
    {
        foreach (var c in SourceChoices) c.IsChecked = true;
        ApplyFilter();
    }

    [RelayCommand]
    public void UncheckAllSources()
    {
        foreach (var c in SourceChoices) c.IsChecked = false;
        ApplyFilter();
    }

    /// <summary>Cancels the live operation on one row (row Cancel button).</summary>
    [RelayCommand]
    public void CancelActiveOperation(PackageInfo? package)
    {
        if (package?.ActiveOperation is { } op)
            Module.Queue.Cancel(op);
    }

    public void NotifySelectionChanged() => OnPropertyChanged(nameof(SelectedCount));

    /// <summary>
    /// Runs an action on the UI thread when there is one, inline otherwise
    /// (keeps ViewModels unit-testable off-thread).
    /// </summary>
    protected Task MarshalAsync(Action action)
    {
        if (_dispatcher != null)
        {
            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }
        action();
        return Task.CompletedTask;
    }

    public IReadOnlyList<PackageInfo> SelectedOr(params PackageInfo[] fallback)
    {
        var selected = Filtered.Where(p => p.IsSelected).ToList();
        return selected.Count > 0 ? selected : fallback.Where(f => f != null).ToList();
    }
}
