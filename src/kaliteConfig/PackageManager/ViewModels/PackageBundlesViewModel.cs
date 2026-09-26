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
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>One bundle row (missing count resolved on demand).</summary>
public sealed partial class BundleRow : ObservableObject
{
    [ObservableProperty] public partial PackageBundle Bundle { get; set; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int MissingCount { get; set; } = -1;
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public string Summary =>
        $"{Bundle.Items.Count} package{(Bundle.Items.Count == 1 ? "" : "s")}" +
        (MissingCount < 0 ? "" : $" • {MissingCount} missing") +
        $" • {Bundle.CreatedAt:yyyy-MM-dd}";
}

/// <summary>Package bundles: list, create, export/import JSON, diff, bulk-install missing.</summary>
public sealed partial class PackageBundlesViewModel : ObservableObject
{
    private readonly PackageManagerModule Module = PackageManagerModule.Instance;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public PackageBundlesViewModel()
    {
        try { _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { _dispatcher = null; }
    }

    public ObservableCollection<BundleRow> Bundles { get; } = new();

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusLine { get; set; } = string.Empty;

    [RelayCommand]
    public void Refresh()
    {
        Module.Bundles.SeedEssentials();
        Bundles.Clear();
        foreach (var b in Module.Bundles.LoadAll())
            Bundles.Add(new BundleRow { Bundle = b });
        if (Bundles.Count == 0) StatusLine = "No bundles yet - add a selection to a bundle from any list.";
        else StatusLine = "";
    }

    public PackageBundle Create(string name, IEnumerable<PackageInfo> selection)
    {
        var bundle = new PackageBundle { Name = name };
        foreach (var p in selection)
            bundle.Items.Add(new BundleItem { PackageId = p.Id, SourceId = p.SourceId, DisplayName = p.DisplayName });
        Module.Bundles.Save(bundle);
        Refresh();
        return bundle;
    }

    [RelayCommand]
    public void DeleteBundle(BundleRow? row)
    {
        if (row is null) return;
        Module.Bundles.Delete(row.Bundle);
        Refresh();
    }

    /// <summary>Diffs one bundle against currently installed (all enabled sources).</summary>
    public async Task<IReadOnlyList<BundleItem>> CheckMissingAsync(BundleRow row, CancellationToken ct)
    {
        row.IsBusy = true;
        try
        {
            var installed = new List<PackageInfo>();
            foreach (var source in Module.Sources.Sources)
            {
                if (!Module.Sources.IsEnabled(source) || !source.CanListInstalled) continue;
                try { installed.AddRange(await source.GetInstalledAsync(ct)); }
                catch { }
            }
            var missing = PackageBundleService.DiffMissing(row.Bundle, installed);
            await MarshalUiAsync(() => row.MissingCount = missing.Count);
            return missing;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Bulk-installs the missing items (continues past failures).</summary>
    public async Task<IReadOnlyList<(BundleItem Item, PackageOperationResult Result)>> InstallMissingAsync(
        BundleRow row, IReadOnlyList<BundleItem> missing, CancellationToken ct)
    {
        var outcomes = new List<(BundleItem, PackageOperationResult)>();
        foreach (var item in missing)
        {
            var source = Module.Sources.Find(item.SourceId);
            if (source is null || !source.CanInstall)
            {
                outcomes.Add((item, PackageOperationResult.Fail("Source cannot install.")));
                continue;
            }
            var pkg = new PackageInfo { Id = item.PackageId, Name = item.DisplayName, SourceId = item.SourceId };
            var result = await Module.Queue.EnqueueAsync("Install", pkg,
                (progress, token) => source.InstallAsync(pkg, null, progress, token), ct);
            outcomes.Add((item, result));
            if (ct.IsCancellationRequested) break;
        }
        return outcomes;
    }

    private Task MarshalUiAsync(Action action)
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
}
