using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace kaliteConfig.Models
{
    public partial class BrowserInstallItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ImagePath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Winget package id (e.g. "Git.Git"). When set, install runs
        /// `winget install --exact --silent` instead of the URL download flow.
        /// </summary>
        [ObservableProperty]
        public partial string WingetId { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string SilentInstallArgs { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstallerFileName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string InstalledCheckPath { get; set; } = string.Empty;

        /// <summary>
        /// For portable tools (zip downloads with no installer): directory the payload is
        /// extracted into under %PROGRAMDATA%. Empty for regular installers.
        /// </summary>
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
        public partial BrowserInstallStatus Status { get; set; } = BrowserInstallStatus.NotInstalled;

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
            BrowserInstallStatus.AlreadyInstalled => "Already installed",
            BrowserInstallStatus.Installed => "Done",
            BrowserInstallStatus.Failed => "Failed",
            _ => ""
        };

        public ObservableCollection<ExtensionItem> Extensions { get; set; } = new();
    }

    public enum BrowserInstallStatus
    {
        NotInstalled,
        Downloading,
        Installing,
        AlreadyInstalled,
        Installed,
        Failed
    }
}
