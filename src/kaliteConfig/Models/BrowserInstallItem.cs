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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

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
        [NotifyPropertyChangedFor(nameof(StatusDetail))]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial double DownloadProgress { get; set; } = 0;

        [ObservableProperty]
        public partial bool IsIndeterminate { get; set; } = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(StatusDetail))]
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

        public string StatusDetail => !string.IsNullOrWhiteSpace(ErrorMessage)
            ? Status == BrowserInstallStatus.Failed ? ErrorMessage : $"{StatusText} - {ErrorMessage}"
            : StatusText;

        public bool HasExtensions => Extensions.Count > 0;

        public string ExtensionsLabel => $"Extensions ({Extensions.Count(e => e.IsSelected)})";

        public BrowserInstallItem()
        {
            Extensions.CollectionChanged += OnExtensionsChanged;
        }

        private void OnExtensionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (ExtensionItem ext in e.NewItems) ext.PropertyChanged += OnExtensionPropertyChanged;
            if (e.OldItems is not null)
                foreach (ExtensionItem ext in e.OldItems) ext.PropertyChanged -= OnExtensionPropertyChanged;
            OnPropertyChanged(nameof(HasExtensions));
            OnPropertyChanged(nameof(ExtensionsLabel));
        }

        private void OnExtensionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ExtensionItem.IsSelected))
                OnPropertyChanged(nameof(ExtensionsLabel));
        }
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
