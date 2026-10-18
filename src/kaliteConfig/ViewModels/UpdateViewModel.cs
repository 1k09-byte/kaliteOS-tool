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
using kaliteConfig.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

public sealed partial class UpdateViewModel : ObservableObject
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

    public string BannerTitle => IsAvailable ? $"kaliteConfig {LatestVersion} available" : string.Empty;

    public bool CanUpdateNow => IsAvailable && !IsBusy;

    [ObservableProperty]
    public partial bool PreferHandOffToInstalledCopy { get; set; }

    [ObservableProperty]
    public partial bool LastAttemptFailed { get; set; }

    [ObservableProperty]
    public partial bool SuppressStartupOffer { get; set; }

    public string? PendingInstallerPath { get; private set; }

    partial void OnPreferHandOffToInstalledCopyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateNow));
        OnPropertyChanged(nameof(PrimaryUpdateActionText));
    }

    partial void OnLastAttemptFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpenInstaller));
        OnPropertyChanged(nameof(PrimaryUpdateActionText));
    }

    public bool CanOpenInstaller =>
        LastAttemptFailed
        && !string.IsNullOrWhiteSpace(PendingInstallerPath)
        && File.Exists(PendingInstallerPath);

    public string PrimaryUpdateActionText =>
        PreferHandOffToInstalledCopy ? "Open updated copy"
        : CanOpenInstaller ? "Open installer"
        : "Update now";

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

    [RelayCommand]
    public async Task CheckForUpdateAsync()
    {
        if (IsBusy) return;
        try
        {
            var pending = UpdateCheckService.ReadPendingUpdate();
            var running = UpdateCheckService.CurrentVersion;
            var runningPath = Environment.ProcessPath;
            var installedExe = UpdateCheckService.GetInstalledExePath();
            var installedVer = installedExe is not null
                ? UpdateCheckService.TryReadExeVersion(installedExe)
                : null;

            var attempt = UpdateCheckService.ClassifyPendingAttempt(pending, running, installedVer);
            if (attempt == UpdateCheckService.UpdateAttemptState.Applied)
            {
                UpdateCheckService.ClearPendingUpdate();
                pending = null;
                attempt = UpdateCheckService.UpdateAttemptState.None;
            }

            var release = await _service.CheckAsync();
            if (release is null) return;

            _pending = release;
            LatestVersion = release.Version;
            Notes = string.IsNullOrWhiteSpace(release.Notes)
                ? "No release notes provided."
                : release.Notes;

            var sameAttempt = pending is not null
                && string.Equals(pending.Version, release.Version, StringComparison.OrdinalIgnoreCase);

            var failed = attempt == UpdateCheckService.UpdateAttemptState.Failed && sameAttempt;
            PendingInstallerPath = failed ? pending!.InstallerPath : null;
            LastAttemptFailed = failed;
            SuppressStartupOffer = failed && pending!.Reported;

            PreferHandOffToInstalledCopy =
                attempt == UpdateCheckService.UpdateAttemptState.AppliedElsewhere
                && sameAttempt
                && installedExe is not null;

            if (failed)
            {
                var reason = UpdateCheckService.ReadInstallerLogSummary(pending!.LogPath);
                Notes += $"\n\nThe update to {pending.Version} did not complete - this copy still reports " +
                         $"v{running ?? "the previous version"}.";
                if (!string.IsNullOrWhiteSpace(reason))
                    Notes += $"\nInstaller reported: {reason}";
                if (!string.IsNullOrWhiteSpace(pending.LogPath) && File.Exists(pending.LogPath))
                    Notes += $"\nFull installer log: {pending.LogPath}";
                Notes += CanOpenInstaller
                    ? "\nChoose Open installer to run it again with its own window, so any error stays on screen."
                    : "\nChoose Update now to download a fresh copy and try again.";
            }
            else if (PreferHandOffToInstalledCopy && installedExe is not null)
            {
                Notes += $"\n\nThe update is already installed at:\n{installedExe}\n" +
                         $"This session is still running v{running ?? "?"} from:\n{runningPath ?? "?"}\n\n" +
                         "Choose Open updated copy to switch (or pin the Start Menu shortcut).";
            }

            IsAvailable = true;
            OnPropertyChanged(nameof(CanOpenInstaller));
            OnPropertyChanged(nameof(PrimaryUpdateActionText));
            OnPropertyChanged(nameof(BannerTitle));
        }
        catch {  }
    }

    [RelayCommand]
    public void HandOffToInstalledCopy()
    {
        if (IsBusy) return;
        if (!UpdateCheckService.TryLaunchInstalledCopyAndExit())
            StatusText = "Could not open the installed copy. Use the Start Menu kaliteConfig shortcut.";
    }

    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        if (IsBusy || _pending is null) return;
        if (PreferHandOffToInstalledCopy)
        {
            HandOffToInstalledCopy();
            return;
        }
        if (CanOpenInstaller)
        {
            OpenInstallerVisibly(PendingInstallerPath!);
            return;
        }

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
            var logPath = UpdateCheckService.SetupLogPath(_pending.Version);
            UpdateCheckService.WritePendingUpdate(_pending.Version, installerPath, logPath);
            var installDir = UpdateCheckService.GetRegisteredInstallDirectory();
            var installArgs = UpdateCheckService.BuildSilentInstallerArguments(installDir, logPath);
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

    private void OpenInstallerVisibly(string installerPath)
    {
        try
        {
            UpdateCheckService.LogDiag($"update: re-running installer visibly {installerPath}");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
            };
            try
            {
                if (App.MainWindow is MainWindow mainWindow)
                    mainWindow.PrepareForUpdateShutdown();
            }
            catch { }

            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            UpdateCheckService.LogDiag($"update: visible installer run failed - {ex.Message}");
            StatusText = $"Could not start the installer: {ex.Message}";
        }
    }
}
