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
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>Software Updates: aggregate upgradable rows, bulk update, CSV export.</summary>
public sealed partial class SoftwareUpdatesViewModel : PackageListViewModelBase
{
    protected override bool SourceEligible(IPackageSource source) => source.CanListUpdates;

    public SoftwareUpdatesViewModel() => RebuildSourceChoices();

    public int UpdateCount => Items.Count;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        var ids = CheckedSourceIds();
        if (ids.Count == 0) { StatusLine = "No sources selected."; return; }
        IsBusy = true;
        StatusLine = "Checking for updates…";
        try
        {
            var found = new List<PackageInfo>();
            foreach (var source in Module.Sources.Sources)
            {
                if (!ids.Contains(source.SourceId) || !source.CanListUpdates) continue;
                try
                {
                    var rows = await source.GetAvailableUpdatesAsync(ct);
                    found.AddRange(rows.Where(p => p.HasUpdate));
                }
                catch (Exception ex)
                {
                    StatusLine = $"{source.DisplayName}: {ex.Message}";
                }
            }
            await MarshalAsync(() => ReplaceItems(found));
            LastCheckedAt = DateTime.Now;
            OnPropertyChanged(nameof(UpdateCount));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceItems(List<PackageInfo> found)
    {
        Items.Clear();
        foreach (var p in found.OrderBy(p => p.Name)) Items.Add(p);
        ApplyFilter();
        if (Filtered.Count == 0 && string.IsNullOrEmpty(StatusLine)) StatusLine = "Everything is up to date.";
        OnPropertyChanged(nameof(UpdateCount));
    }

    protected override void RefreshCounts()
    {
        string when = LastCheckedAt is null ? "never checked"
            : $"checked {LastCheckedAt:HH:mm:ss}";
        CountsLine = $"{Filtered.Count} update{(Filtered.Count == 1 ? "" : "s")} • {SelectedCount} selected • {when}";
    }

    [RelayCommand]
    public async Task UpdateOneAsync(PackageInfo? package)
    {
        if (package is null) return;
        var outcomes = await UpdateSelectionAsync(new[] { package }, CancellationToken.None);
        StatusLine = outcomes[0].Result.Success
            ? $"Updated {package.DisplayName}."
            : outcomes[0].Result.Message;
        await LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task UpdateSelectedAsync(CancellationToken ct)
    {
        var selection = SelectedOr();
        if (selection.Count == 0) { StatusLine = "Nothing selected."; return; }
        var outcomes = await UpdateSelectionAsync(selection, ct);
        int ok = outcomes.Count(o => o.Result.Success);
        int failed = outcomes.Count - ok;
        StatusLine = failed == 0
            ? $"Updated {ok} package{(ok == 1 ? "" : "s")}."
            : $"{ok} updated, {failed} failed: " + string.Join("; ",
                outcomes.Where(o => !o.Result.Success).Take(3).Select(o => $"{o.Item.DisplayName}: {o.Result.Message}"));
        await LoadAsync(CancellationToken.None);
    }

    /// <summary>Queues updates for the selection (bulk continues past failures).</summary>
    public async Task<IReadOnlyList<(PackageInfo Item, PackageOperationResult Result)>> UpdateSelectionAsync(
        IReadOnlyList<PackageInfo> selection, CancellationToken ct)
    {
        var outcomes = new List<(PackageInfo, PackageOperationResult)>();
        foreach (var pkg in selection)
        {
            var source = Module.Sources.Find(pkg.SourceId);
            if (source is null || !source.CanUpdate)
            {
                outcomes.Add((pkg, PackageOperationResult.Fail("Source cannot update.")));
                continue;
            }
            var result = await Module.Queue.EnqueueAsync("Update", pkg,
                (progress, token) => source.UpdateAsync(pkg, progress, token), ct);
            outcomes.Add((pkg, result));
            if (ct.IsCancellationRequested) break;
        }
        return outcomes;
    }
}
