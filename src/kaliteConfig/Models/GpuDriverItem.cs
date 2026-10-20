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
using Microsoft.UI.Xaml;
using System;

namespace kaliteConfig.Models
{
    public partial class GpuDriverItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Vendor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LatestVersionVis))]
        public partial string InstalledVersion { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        [NotifyPropertyChangedFor(nameof(LatestVersionVis))]
        public partial string LatestVersion { get; set; } = string.Empty;

        public Visibility LatestVersionVis =>
            Status == GpuDriverStatus.UpToDate
            || string.IsNullOrWhiteSpace(LatestVersion)
            || string.Equals(InstalledVersion?.Trim(), LatestVersion.Trim(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;

        [ObservableProperty]
        public partial string DownloadUrl { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SilentInstallArgs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstallerFileName { get; set; } = string.Empty;

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

        [ObservableProperty]
        public partial string VendorPageUrl { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool GuidedInstallOnly { get; set; }

        [ObservableProperty]
        public partial bool IsPageOnly { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
        [NotifyPropertyChangedFor(nameof(CheckActionText))]
        [NotifyPropertyChangedFor(nameof(IsCheckEnabled))]
        [NotifyPropertyChangedFor(nameof(LatestVersionVis))]
        [NotifyPropertyChangedFor(nameof(StateLabel))]
        [NotifyPropertyChangedFor(nameof(StateGlyph))]
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

        public string StateLabel => Status switch
        {
            GpuDriverStatus.UpToDate => "Up to date",
            GpuDriverStatus.UpdateAvailable => "Update available",
            GpuDriverStatus.NotInstalled => "Not installed",
            GpuDriverStatus.NotChecked => "Not checked",
            GpuDriverStatus.Downloading => "Downloading",
            GpuDriverStatus.Installing => "Installing",
            GpuDriverStatus.Installed => "Installed",
            GpuDriverStatus.Failed => "Failed",
            GpuDriverStatus.ManualActionRequired => "Action needed",
            _ => "Unknown"
        };

        public string StateGlyph => Status switch
        {
            GpuDriverStatus.UpToDate => "\uE73E",
            GpuDriverStatus.UpdateAvailable => "\uE895",
            GpuDriverStatus.NotInstalled => "\uE7B8",
            GpuDriverStatus.Downloading => "\uE896",
            GpuDriverStatus.Installing => "\uE895",
            GpuDriverStatus.Installed => "\uE73E",
            GpuDriverStatus.Failed => "\uE783",
            GpuDriverStatus.ManualActionRequired => "\uE7BA",
            _ => "\uE946"
        };

        [ObservableProperty]
        public partial bool IsSlimmerBusy { get; set; }

        [ObservableProperty]
        public partial bool IsSlimmerInstalled { get; set; }

        [ObservableProperty]
        public partial double SlimmerProgress { get; set; } = -1;
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
