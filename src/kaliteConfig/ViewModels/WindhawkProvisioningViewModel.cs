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
using kaliteConfig.Models;
using kaliteConfig.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

namespace kaliteConfig.ViewModels;

/// <summary>
/// Drives the "Install Windhawk &amp; import settings" flow on the WS page:
/// detection → user confirmation dialog → download/silent install (if needed)
/// → backup import (via windhawk-cli data import), then optional mod updates,
/// with progress reporting and a per-mod summary.
/// </summary>
public partial class WindhawkProvisioningViewModel : ObservableObject
{
    private readonly WindhawkDetectionService _detection = new();
    private readonly WindhawkProvisioningService _provisioning = new();
    private readonly WindhawkInstallerService _installer = new();
    private readonly WindhawkImportService _import = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    public partial WindhawkInstallationInfo Installation { get; set; } = WindhawkInstallationInfo.NotInstalled;
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial double? DownloadPercent { get; set; }
    [ObservableProperty]
    public partial bool HasMessage { get; set; }
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;
    [ObservableProperty]
    public partial InfoBarSeverity MessageSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>
    /// KaliteOS bundled file - always from Assets/Windhawk/KaliteOS.json (copied to mods-bundled.json).
    /// No backup-file picker; the AutoOS remote is never used.
    /// </summary>
    public string? EffectiveBackupFilePath => WindhawkModCatalog.EnsureBundledBackupOnDisk();

    public bool IsInstalled => Installation.IsInstalled;
    public bool IsCliAvailable => Installation.CliPath is not null;
    public string InstallStatusText => Installation.IsInstalled
        ? $"Installed{(!string.IsNullOrEmpty(Installation.Version) ? " · v" + Installation.Version : "")}"
        : "Not installed";

    partial void OnInstallationChanged(WindhawkInstallationInfo value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsCliAvailable));
        OnPropertyChanged(nameof(InstallStatusText));
        OnPropertyChanged(nameof(CanTestImport));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ShowLegacyUpgradeNotice));
    }

    public bool CanRun => !IsBusy && !string.IsNullOrEmpty(EffectiveBackupFilePath) && File.Exists(EffectiveBackupFilePath);
    // Test import is only valid when Windhawk is actually installed (mirrors AutoOS: windhawk-cli.exe must exist)
    public bool CanTestImport => !IsBusy && Installation.IsInstalled && !string.IsNullOrEmpty(EffectiveBackupFilePath) && File.Exists(EffectiveBackupFilePath);

    /// <summary>InfoBar notice: a legacy (pre-2.0) install was detected and the
    /// flow will upgrade it to the pinned 2.0 alpha so settings import works.</summary>
    public bool ShowLegacyUpgradeNotice => Installation.IsInstalled && Installation.CliPath is null;

    /// <summary>XAML-friendly busy indicator (Collapsed/Visible).</summary>
    public Microsoft.UI.Xaml.Visibility BusyVisibility => IsBusy
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public double DownloadPercentValue => DownloadPercent ?? 0;
    public bool IsProgressIndeterminate => IsBusy && DownloadPercent == null;
    public Microsoft.UI.Xaml.Visibility DeterminateProgressVisibility => IsBusy && DownloadPercent.HasValue
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility IndeterminateProgressVisibility => IsBusy && !DownloadPercent.HasValue
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility StatusTextVisibility => !IsBusy || string.IsNullOrWhiteSpace(StatusText)
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanTestImport));
        OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(DeterminateProgressVisibility));
        OnPropertyChanged(nameof(IndeterminateProgressVisibility));
        OnPropertyChanged(nameof(StatusTextVisibility));
        OnPropertyChanged(nameof(DownloadPercentValue));
    }

    partial void OnDownloadPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(DownloadPercentValue));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(DeterminateProgressVisibility));
        OnPropertyChanged(nameof(IndeterminateProgressVisibility));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(StatusTextVisibility));
    }

    public void RefreshDetection()
    {
        Installation = _detection.Detect();
        if (Installation.IsInstalled)
        {
            ShowMessage($"Windhawk {Installation.Version ?? ""} detected.".Trim() + (Installation.CliPath is not null
                ? " Settings import will use its built-in CLI."
                : " Running the flow upgrades to Windhawk 2.0 (alpha 5) - its CLI is required for settings import."),
                InfoBarSeverity.Informational);
        }
        else
        {
            ShowMessage("Windhawk is not installed. Running the flow below installs Windhawk 2.0 (alpha 5) silently, then imports the bundled settings.",
                InfoBarSeverity.Informational);
        }
    }

    public void ShowMessage(string message, InfoBarSeverity severity)
    {
        Message = message;
        MessageSeverity = severity;
        HasMessage = true;
    }

    private void ClearMessage() => HasMessage = false;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// The whole flow, awaited by the page after the confirmation ContentDialog
    /// resolves. Runs install (when missing) and import sequentially with
    /// progress surfaced through observable properties.
    /// </summary>
    public async Task RunProvisioningAsync()
    {
        var path = EffectiveBackupFilePath;
        if (string.IsNullOrEmpty(path)) return;

        IsBusy = true;
        ClearMessage();
        DownloadPercent = null;
        StatusText = "Starting...";
        _cts = new CancellationTokenSource();

        try
        {
            if (!Installation.IsInstalled)
            {
                if (!WindhawkDetectionService.IsRunningElevated())
                {
                    ShowMessage("Administrator rights are required to install Windhawk. Run kaliteConfig elevated and try again.",
                        InfoBarSeverity.Error);
                    return;
                }

                var installProgress = new Progress<WindhawkProvisioningService.ProvisionProgress>(p =>
                {
                    StatusText = p.StatusText;
                    DownloadPercent = p.DownloadPercent;
                });

                StatusText = "Preparing installation...";
                Installation = await _provisioning.EnsureInstalledAsync(installProgress, _cts.Token);
            }

            // Prefer the verified CLI import path (Windhawk 2.0+): download the
            // offline installer, install it silently, then import via windhawk-cli
            // data import, and apply any available mod updates.
            var cliProgress = new Progress<WindhawkProvisioningService.ProvisionProgress>(p =>
            {
                StatusText = p.StatusText;
                DownloadPercent = p.DownloadPercent;
            });

            StatusText = "Importing KaliteOS settings...";
            DownloadPercent = null;

            var importStatus = new Progress<string>(s => StatusText = s);
            var importResult = await _import.ImportBackupAsync(path!, Installation, importStatus, _cts.Token);
            
            ShowMessage(importResult.SummaryText, importResult.Skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            
            StatusText = "Done.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            ShowMessage("Operation cancelled.", InfoBarSeverity.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText = "Failed.";
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            StatusText = "Failed.";
            ShowMessage($"Windhawk provisioning failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            DownloadPercent = null;
            _cts?.Dispose();
            _cts = null;
            // Re-detect: an install just happened (or state changed underneath us).
            Installation = _detection.Detect();
        }
    }

    /// <summary>
    /// Test-only import - does NOT reinstall Windhawk, just runs the settings import.
    /// Copied flow from AutoOS AppsStage.cs:
    ///   await DownloadHelper.Download(jsonUrl, Path.GetTempPath(), "windhawk.json");
    ///   await Process.Start(windhawk-cli.exe, $"data import \"{json}\" --confirm-app-restart --yes", WorkingDir=Windhawk).WaitForExitAsync();
    ///   await ProcessActions.UpdateWindhawkMods();
    /// This is exposed as a separate "Test Import Settings" button in the UI so the import
    /// can be verified without re-downloading/re-installing the Windhawk binary.
    /// </summary>
    [RelayCommand]
    public async Task TestImportAsync()
    {
        string? path = EffectiveBackupFilePath;

        // Always use the KaliteOS bundled file from Assets/Windhawk/KaliteOS.json (via WindhawkModCatalog).
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ShowMessage($"KaliteOS settings file not found at: {path ?? "(null)"}", InfoBarSeverity.Error);
            return;
        }

        if (!Installation.IsInstalled)
        {
            ShowMessage("Windhawk is not installed - install it first before testing import.", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        ClearMessage();
        StatusText = "Testing import (windhawk-cli data import)...";
        _cts = new CancellationTokenSource();

        try
        {
            // Prefer the installer service's CLI import (AutoOS-style) when CLI is available,
            // otherwise fall back to the generic import service (registry path).
            if (!string.IsNullOrEmpty(Installation.CliPath) && File.Exists(Installation.CliPath!))
            {
                var progress = new Progress<string>(s => StatusText = s);
                var result = await _installer.ImportSettingsAsync(path!, progress, _cts.Token);
                ShowMessage(
                    result.Success
                        ? $"Test import succeeded (exit {result.ExitCode}): {result.Stdout.Trim().Split('\n').LastOrDefault()?.Trim() ?? "ok"}"
                        : $"Test import returned exit {result.ExitCode}: {(string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim()}",
                    result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                StatusText = result.Success ? "Test import done." : "Test import had issues.";
            }
            else
            {
                var progress = new Progress<string>(s => StatusText = s);
                var importResult = await _import.ImportBackupAsync(path!, Installation, progress, _cts.Token);
                ShowMessage($"[Test] {importResult.SummaryText}", importResult.Skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
                StatusText = "Test import done.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            ShowMessage("Test import cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StatusText = "Failed.";
            ShowMessage($"Test import failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            Installation = _detection.Detect();
        }
    }
}
