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
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace kaliteConfig.Models
{
    public partial class BrowserInstallItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        public string ProductId { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ImagePath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DownloadUrl { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string WingetId { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SilentInstallArgs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstallerFileName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstalledCheckPath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ToolInstallDir { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial double DownloadProgress { get; set; } = 0;

        [ObservableProperty]
        public partial bool IsIndeterminate { get; set; } = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial BrowserInstallStatus Status { get; set; } = BrowserInstallStatus.NotInstalled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial bool UpdateAvailable { get; set; } = false;

        [ObservableProperty]
        public partial string InstalledVersion { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string LatestVersion { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial double InstallProgress { get; set; } = 0;

        [ObservableProperty]
        public partial bool IsVisible { get; set; } = true;

        public string StatusText => Status switch
        {
            BrowserInstallStatus.NotInstalled => "Install",
            BrowserInstallStatus.Downloading => $"Downloading {InstallProgress:F0}%",
            BrowserInstallStatus.Installing => "Installing...",
            BrowserInstallStatus.AlreadyInstalled => UpdateAvailable ? "Update available" : "Already installed",
            BrowserInstallStatus.UpdateAvailable => "Update",
            BrowserInstallStatus.Installed => "Done",
            BrowserInstallStatus.Failed => "Failed",
            _ => ""
        };

        public ObservableCollection<ExtensionItem> Extensions { get; set; } = new();
    }

    public enum AppViewMode
    {
        Card,

        Compact,
    }

    public enum BrowserInstallStatus
    {
        NotInstalled,
        Downloading,
        Installing,
        AlreadyInstalled,
        Installed,
        UpdateAvailable,
        Failed
    }
}
