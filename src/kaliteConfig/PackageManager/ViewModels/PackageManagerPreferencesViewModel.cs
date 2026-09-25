using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.ViewModels;

/// <summary>Package Managers preferences: detection status, enable toggles, exe overrides.</summary>
public sealed partial class PackageManagerPreferencesViewModel : ObservableObject
{
    protected readonly PackageManagerModule Module = PackageManagerModule.Instance;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    public PackageManagerPreferencesViewModel()
    {
        try { _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { _dispatcher = null; }
    }

    public ObservableCollection<PackageSourceStatus> Rows { get; } = new();

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusLine { get; set; } = string.Empty;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusLine = "Detecting package managers…";
        try
        {
            var statuses = await Module.Sources.DetectAsync(ct);
            await MarshalUiAsync(() =>
            {
                Rows.Clear();
                foreach (var s in statuses) Rows.Add(s);
                StatusLine = "";
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SetEnabled(PackageSourceStatus row, bool enabled)
    {
        Module.Sources.SetEnabled(row.SourceId, enabled);
        row.IsEnabled = enabled;
    }

    public void SetExecutableOverride(string sourceId, string? path)
    {
        Module.Sources.SetExecutableOverride(sourceId, path);
    }

    private Task MarshalUiAsync(System.Action action)
    {
        if (_dispatcher != null)
        {
            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (System.Exception ex) { tcs.SetException(ex); } });
            return tcs.Task;
        }
        action();
        return Task.CompletedTask;
    }
}
