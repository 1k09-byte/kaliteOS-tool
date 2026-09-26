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
using Microsoft.UI.Xaml;

namespace kaliteConfig.Models
{
    public partial class GpuDriverItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        // NVIDIA / AMD / Intel
        [ObservableProperty]
        public partial string Vendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        // Driver version currently on this machine ("" when nothing usable detected).
        [ObservableProperty]
        public partial string InstalledVersion { get; set; } = string.Empty;

        // Latest version from the vendor lookup ("" until checked).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        public partial string LatestVersion { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DownloadUrl { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SilentInstallArgs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstallerFileName { get; set; } = string.Empty;

        // --- UI BINDING (observable so detection results refresh live) ---
        [ObservableProperty]
        public partial string HardwareName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string VramText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string GpuTypeText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DeviceTypeText { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PrimaryVis))]
        [NotifyPropertyChangedFor(nameof(SecondaryVis))]
        public partial bool IsPrimary { get; set; } = false;

        public Visibility PrimaryVis => IsPrimary ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SecondaryVis => IsPrimary ? Visibility.Collapsed : Visibility.Visible;
        // -------------------------------------

        // Check-button state: the button itself carries "up to date" once a
        // check has confirmed it (disabled - nothing to do), stays actionable
        // otherwise, and goes quiet while a download/install is running.

        // Fallback official page (used when lookup/download fails, and for page-only vendors).
        [ObservableProperty]
        public partial string VendorPageUrl { get; set; } = string.Empty;

        // True when the vendor installer has no reliable silent flags (AMD/Intel):
        // the app downloads (if a URL is known) then launches the vendor UI.
        [ObservableProperty]
        public partial bool GuidedInstallOnly { get; set; }

        // True when there is no direct download at all (Intel): primary button opens the page.
        [ObservableProperty]
        public partial bool IsPageOnly { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        [NotifyPropertyChangedFor(nameof(CheckActionText))]
        [NotifyPropertyChangedFor(nameof(IsCheckEnabled))]
        public partial GpuDriverStatus Status { get; set; } = GpuDriverStatus.NotChecked;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial double DownloadProgress { get; set; } = 0;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsVisible { get; set; } = true;

        public string StatusText => Status switch
        {
            GpuDriverStatus.NotChecked => "Check for updates",
            GpuDriverStatus.NotInstalled => "Install",
            GpuDriverStatus.UpdateAvailable => string.IsNullOrEmpty(LatestVersion) ? "Update available" : $"Update available · {LatestVersion}",
            GpuDriverStatus.UpToDate => string.IsNullOrEmpty(InstalledVersion) ? "Up to date" : $"Up to date · {InstalledVersion}",
            GpuDriverStatus.Downloading => $"Downloading {DownloadProgress:F0}%",
            GpuDriverStatus.Installing => "Installing...",
            GpuDriverStatus.Installed => "Done",
            GpuDriverStatus.Failed => "Failed",
            GpuDriverStatus.ManualActionRequired => "Continue in the vendor installer",
            _ => ""
        };

        public string PrimaryActionText => Status switch
        {
            _ when IsPageOnly => "Open download center",
            GpuDriverStatus.NotChecked => "Check for updates",
            GpuDriverStatus.NotInstalled => "Install",
            GpuDriverStatus.UpdateAvailable => "Update now",
            GpuDriverStatus.UpToDate => "Reinstall",
            GpuDriverStatus.Failed => "Retry",
            _ => "Install"
        };

        public string CheckActionText => Status switch
        {
            GpuDriverStatus.UpToDate => "Up to date",
            GpuDriverStatus.Downloading => "Downloading…",
            GpuDriverStatus.Installing => "Installing…",
            _ => "Check for updates",
        };

        public bool IsCheckEnabled => Status is not (
            GpuDriverStatus.UpToDate or GpuDriverStatus.Downloading or GpuDriverStatus.Installing);
    }

    public enum GpuDriverStatus
    {
        NotChecked,
        NotInstalled,
        UpdateAvailable,
        UpToDate,
        Downloading,
        Installing,
        Installed,
        Failed,
        ManualActionRequired
    }
}
