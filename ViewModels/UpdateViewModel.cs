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

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }
    [ObservableProperty]
    public partial string LatestVersion { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    public partial double? DownloadPercent { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

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
            // Loop guard: this exact version was already installed once but the
            // running app still reports older — the install didn't take (wrong
            // folder, dev copy, or a side-by-side older flavor). Say so instead
            // of looping the same offer silently.
            var pending = UpdateCheckService.ReadPendingUpdateVersion();
            if (!string.IsNullOrEmpty(pending)
                && string.Equals(pending, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                Notes += "\n\nNote: an update to this version was already installed once, but this copy still " +
                         "reports the older version — the install didn't take. You may be launching kaliteConfig " +
                         "from a different folder (a dev build, or the old separate Consumer install — uninstall " +
                         "any 'kaliteConfig Consumer' copy once and launch from the Start Menu).";
            }
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
            var info = new FileInfo(installerPath);
            UpdateCheckService.LogDiag($"update: downloaded {info.Name} ({info.Length} bytes) for version {_pending.Version}");

            StatusText = "Launching installer…";
            // Inno Setup silent flags (/CLOSEAPPLICATIONS is the real one —
            // /FORCECLOSEAPPLICATIONS does not exist and was silently ignored,
            // leaving a locked exe to fail the file replacement):
            // /VERYSILENT no wizard, /SUPPRESSMSGBOXES no popups, /NORESTART,
            // /CLOSEAPPLICATIONS lets Inno close a still-running instance.
            // The app runs elevated (requireAdministrator manifest), so the
            // child inherits elevation.
            // Remember this version BEFORE launching: if the app restarts and
            // still offers it, the install didn't take (see CheckForUpdateAsync).
            UpdateCheckService.WritePendingUpdate(_pending.Version);
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            UpdateCheckService.LogDiag($"update: launching {info.Name} for version {_pending.Version}");
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
