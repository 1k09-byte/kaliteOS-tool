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
            var pendingVersion = UpdateCheckService.ReadPendingUpdateVersion();
            var running = UpdateCheckService.CurrentVersion;
            if (!string.IsNullOrEmpty(pendingVersion)
                && !string.IsNullOrEmpty(running)
                && !UpdateCheckService.IsNewer(pendingVersion, running))
            {
                UpdateCheckService.ClearPendingUpdate();
            }

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
            // Silent Inno upgrade: disable CloseApplications (tray swallows
            // WM_CLOSE), target the registered install dir (/DIR), exit hard so
            // files are not locked. Setup also taskkill's in PrepareToInstall.
            UpdateCheckService.WritePendingUpdate(_pending.Version);
            var installDir = UpdateCheckService.GetRegisteredInstallDirectory();
            var installArgs = UpdateCheckService.BuildSilentInstallerArguments(installDir);
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = installArgs,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            UpdateCheckService.LogDiag(
                $"update: launching {info.Name} for version {_pending.Version} args={installArgs}");
            try
            {
                if (App.MainWindow is MainWindow mainWindow)
                    mainWindow.PrepareForUpdateShutdown();
            }
            catch { }

            Process.Start(psi);

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
