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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>Discover: search across checked sources, install selection.</summary>
public sealed partial class DiscoverPackagesViewModel : PackageListViewModelBase
{
    [ObservableProperty] public partial PackageSearchMode SearchMode { get; set; } = PackageSearchMode.Both;
    [ObservableProperty] public partial string Scope { get; set; } = "Default";

    /// <summary>ComboBox-friendly mirror of SearchMode (Name/Id/Both/Exact/Similar order).</summary>
    public int SearchModeIndex
    {
        get => (int)SearchMode;
        set { SearchMode = (PackageSearchMode)value; OnPropertyChanged(); }
    }

    protected override bool SourceEligible(IPackageSource source) => source.CanSearch;

    public DiscoverPackagesViewModel() => RebuildSourceChoices();

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        string query = (SearchQuery ?? "").Trim();
        if (query.Length == 0) { StatusLine = "Type something to search for."; return; }
        var ids = CheckedSourceIds();
        if (ids.Count == 0) { StatusLine = "No sources selected."; return; }
        IsBusy = true;
        StatusLine = $"Searching {query}…";
        try
        {
            var found = new List<PackageInfo>();
            foreach (var source in Module.Sources.Sources)
            {
                if (!ids.Contains(source.SourceId) || !source.CanSearch) continue;
                try
                {
                    var results = await source.SearchAsync(query, SearchMode, ct);
                    found.AddRange(results);
                }
                catch (Exception ex)
                {
                    StatusLine = $"{source.DisplayName}: {ex.Message}";
                }
            }
            
            // Cross-reference with the locally installed packages 
            // so we can surface installed status and correct Winget's parse inversion
            var installedPackages = await Module.GetInstalledPackagesAsync();
            foreach (var pkg in found)
            {
                var installedMatch = installedPackages.FirstOrDefault(i => i.Id != null && pkg.Id != null && (
                    i.Id.Equals(pkg.Id, StringComparison.OrdinalIgnoreCase) || 
                    (i.Id.EndsWith("…") && pkg.Id.StartsWith(i.Id.TrimEnd('…'), StringComparison.OrdinalIgnoreCase) && 
                     i.Name != null && pkg.Name != null && pkg.Name.StartsWith(i.Name.TrimEnd('…'), StringComparison.OrdinalIgnoreCase))
                ));
                
                // Winget Search returns the remote catalog version in the Version column,
                // which gets mistakenly parsed as InstalledVersion. Fix that here:
                if (string.IsNullOrWhiteSpace(pkg.AvailableVersion)) 
                {
                     pkg.AvailableVersion = pkg.InstalledVersion;
                     pkg.InstalledVersion = string.Empty;
                }
                
                if (installedMatch != null)
                {
                    pkg.InstalledVersion = installedMatch.InstalledVersion;
                }
            }

            await MarshalAsync(() =>
            {
                Items.Clear();
                foreach (var p in found.OrderBy(p => p.Name)) Items.Add(p);
                ApplyFilter();
                LastCheckedAt = DateTime.Now;
                StatusLine = Filtered.Count == 0 ? "No packages found." : "";
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task InstallOneAsync(PackageInfo? package)
    {
        if (package is null) return;
        var outcomes = await InstallSelectionAsync(new[] { package }, CancellationToken.None);
        StatusLine = outcomes[0].Result.Success
            ? $"Installed {package.DisplayName}."
            : outcomes[0].Result.Message;
    }

    [RelayCommand]
    public async Task InstallSelectedAsync(CancellationToken ct)
    {
        var selection = SelectedOr();
        if (selection.Count == 0) { StatusLine = "Nothing selected."; return; }
        var outcomes = await InstallSelectionAsync(selection, ct);
        int ok = outcomes.Count(o => o.Result.Success);
        int failed = outcomes.Count - ok;
        StatusLine = failed == 0
            ? $"Installed {ok} package{(ok == 1 ? "" : "s")}."
            : $"{ok} installed, {failed} failed: " + string.Join("; ",
                outcomes.Where(o => !o.Result.Success).Take(3).Select(o => $"{o.Item.DisplayName}: {o.Result.Message}"));
    }

    /// <summary>Queues installs for the selection (bulk continues past failures).</summary>
    public async Task<IReadOnlyList<(PackageInfo Item, PackageOperationResult Result)>> InstallSelectionAsync(
        IReadOnlyList<PackageInfo> selection, CancellationToken ct)
    {
        var outcomes = new List<(PackageInfo, PackageOperationResult)>();
        foreach (var pkg in selection)
        {
            var source = Module.Sources.Find(pkg.SourceId);
            if (source is null || !source.CanInstall)
            {
                outcomes.Add((pkg, PackageOperationResult.Fail("Source cannot install.")));
                continue;
            }
            string? scope = string.Equals(Scope, "Default", StringComparison.OrdinalIgnoreCase) ? null : Scope;
            var result = await Module.Queue.EnqueueAsync("Install", pkg,
                (progress, token) => source.InstallAsync(pkg, scope, progress, token), ct);
            outcomes.Add((pkg, result));
            if (ct.IsCancellationRequested) break;
        }
        return outcomes;
    }

    /// <summary>Adds packages to a bundle (created when missing).</summary>
    public PackageBundle AddToBundle(IEnumerable<PackageInfo> selection, string bundleName)
    {
        var bundle = Module.Bundles.LoadAll()
            .FirstOrDefault(b => b.Name.Equals(bundleName, StringComparison.OrdinalIgnoreCase))
            ?? new PackageBundle { Name = bundleName };
        var known = bundle.Items.Select(i => i.SourceId + "\u0001" + i.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in selection)
        {
            string key = p.SourceId + "\u0001" + p.Id;
            if (known.Add(key))
                bundle.Items.Add(new BundleItem { PackageId = p.Id, SourceId = p.SourceId, DisplayName = p.DisplayName });
        }
        Module.Bundles.Save(bundle);
        return bundle;
    }
}
