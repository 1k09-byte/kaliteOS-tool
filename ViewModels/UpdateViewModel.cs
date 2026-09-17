using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

/// <summary>
/// Drives the consumer update banner: startup check against GitHub Releases,
/// "Update now" flow (download with progress → launch the NSIS setup /S →
/// exit so the installer can overwrite the files).
/// Compiled only in the CONSUMER flavor.
/// </summary>
#if CONSUMER
public sealed partial class UpdateViewModel : ObservableObject
#else
public sealed partial class UpdateViewModel : ObservableObject // full flavor: never instantiated
#endif
{
    private readonly UpdateCheckService _service = new();
    private CancellationTokenSource? _cts;
    private UpdateCheckService.LatestRelease? _pending;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _latestVersion = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double? _downloadPercent;
    [ObservableProperty] private string _statusText = string.Empty;

    public Microsoft.UI.Xaml.Visibility BannerVisibility => IsAvailable
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ProgressVisibility => IsBusy && DownloadPercent is not null
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>XAML binding source for the InfoBar title.</summary>
    public string BannerTitle => IsAvailable ? $"kaliteConfig {LatestVersion} available" : string.Empty;

    public bool CanUpdateNow => IsAvailable && !IsBusy;

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(BannerVisibility));
        OnPropertyChanged(nameof(BannerTitle));
        OnPropertyChanged(nameof(CanUpdateNow));
    }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(CanUpdateNow));
    }
    partial void OnDownloadPercentChanged(double? value) => OnPropertyChanged(nameof(ProgressVisibility));

    /// <summary>Non-blocking startup check; never throws.</summary>
    [RelayCommand]
    public async Task CheckForUpdateAsync()
    {
        if (IsBusy) return;
        try
        {
            var release = await _service.CheckAsync();
            if (release is null) return;

            _pending = release;
            LatestVersion = release.Version;
            Notes = string.IsNullOrWhiteSpace(release.Notes)
                ? "No release notes provided."
                : release.Notes;
            IsAvailable = true;
            OnPropertyChanged(nameof(BannerTitle));
        }
        catch { /* never surface check failures */ }
    }

    /// <summary>Download the setup exe and hand off to the silent installer.</summary>
    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        if (IsBusy || _pending is null) return;

        IsBusy = true;
        DownloadPercent = null;
        StatusText = "Downloading update…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => DownloadPercent = p);
            string installerPath = await _service.DownloadAsync(_pending, progress, _cts.Token);

            StatusText = "Launching installer…";
            // Inno Setup silent flags (NOT NSIS /S — Inno ignores /S, which made
            // the "installed" update a no-op and the app re-prompted forever):
            // /VERYSILENT no wizard, /SUPPRESSMSGBOXES no popups, /NORESTART,
            // /CLOSEAPPLICATIONS lets Inno close a still-running instance.
            // The app runs elevated (requireAdministrator manifest), so the
            // child inherits elevation.
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);

            // Exit so the installer can overwrite the locked exe. The setup
            // runs detached with /FORCECLOSEAPPLICATIONS, but a clean exit
            // here avoids RestartManager aborting the silent install (the
            // close-to-tray handler swallows window closes — observed in the
            // Inno log as "Some applications could not be shut down" →
            // rollback). Exit hard, now.
            await Task.Delay(300);
            Environment.Exit(0);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            DownloadPercent = null;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
