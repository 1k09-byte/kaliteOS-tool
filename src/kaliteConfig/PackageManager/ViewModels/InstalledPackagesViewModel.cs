using CommunityToolkit.Mvvm.Input;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>Installed packages across enabled sources. Uninstall confirms in the view first.</summary>
public sealed partial class InstalledPackagesViewModel : PackageListViewModelBase
{
    protected override bool SourceEligible(IPackageSource source) => source.CanListInstalled;

    public InstalledPackagesViewModel() => RebuildSourceChoices();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        var ids = CheckedSourceIds();
        if (ids.Count == 0) { StatusLine = "No sources selected."; return; }
        IsBusy = true;
        StatusLine = "Loading installed packages…";
        try
        {
            var found = new List<PackageInfo>();
            foreach (var source in Module.Sources.Sources)
            {
                if (!ids.Contains(source.SourceId) || !source.CanListInstalled) continue;
                try
                {
                    found.AddRange(await source.GetInstalledAsync(ct));
                }
                catch (Exception ex)
                {
                    StatusLine = $"{source.DisplayName}: {ex.Message}";
                }
            }
            await MarshalAsync(() =>
            {
                Items.Clear();
                foreach (var p in found.OrderBy(p => p.Name)) Items.Add(p);
                ApplyFilter();
                LastCheckedAt = DateTime.Now;
                if (Filtered.Count == 0 && string.IsNullOrEmpty(StatusLine)) StatusLine = "Nothing installed from these sources.";
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task UninstallOneAsync(PackageInfo? package)
    {
        if (package is null) return;
        var outcomes = await UninstallSelectionAsync(new[] { package }, CancellationToken.None);
        StatusLine = outcomes[0].Result.Success
            ? $"Uninstalled {package.DisplayName}."
            : outcomes[0].Result.Message;
        await LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task UninstallSelectedAsync(CancellationToken ct)
    {
        var selection = SelectedOr();
        if (selection.Count == 0) { StatusLine = "Nothing selected."; return; }
        var outcomes = await UninstallSelectionAsync(selection, ct);
        int ok = outcomes.Count(o => o.Result.Success);
        int failed = outcomes.Count - ok;
        StatusLine = failed == 0
            ? $"Uninstalled {ok} package{(ok == 1 ? "" : "s")}."
            : $"{ok} uninstalled, {failed} failed: " + string.Join("; ",
                outcomes.Where(o => !o.Result.Success).Take(3).Select(o => $"{o.Item.DisplayName}: {o.Result.Message}"));
        await LoadAsync(CancellationToken.None);
    }

    /// <summary>Queues uninstalls for the selection (bulk continues past failures).</summary>
    public async Task<IReadOnlyList<(PackageInfo Item, PackageOperationResult Result)>> UninstallSelectionAsync(
        IReadOnlyList<PackageInfo> selection, CancellationToken ct)
    {
        var outcomes = new List<(PackageInfo, PackageOperationResult)>();
        foreach (var pkg in selection)
        {
            var source = Module.Sources.Find(pkg.SourceId);
            if (source is null || !source.CanUninstall)
            {
                outcomes.Add((pkg, PackageOperationResult.Fail("Source cannot uninstall.")));
                continue;
            }
            var result = await Module.Queue.EnqueueAsync("Uninstall", pkg,
                (progress, token) => source.UninstallAsync(pkg, progress, token), ct);
            outcomes.Add((pkg, result));
            if (ct.IsCancellationRequested) break;
        }
        return outcomes;
    }
}
