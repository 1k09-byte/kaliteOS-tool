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
using System;
using System.Linq;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class DiscoverPackagesPage : Page
    {
        private static readonly string[] InstallerExtensions = { ".crx", ".xpi", ".exe", ".msi" };

        public DiscoverPackagesViewModel Vm { get; } = new();

        public DiscoverPackagesPage()
        {
            this.InitializeComponent();
        }

        public static Visibility EmptyVis(int count, bool busy) =>
            count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;

        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            _ = Vm.SearchCommand.ExecuteAsync(null);
        }

        private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item && item.Content is string scope)
                Vm.Scope = scope;
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag && int.TryParse(tag, out int mode))
                Vm.ViewMode = (PackageViewMode)mode;
        }

        private void ResultsList_SelectionToggled(object sender, PackageInfo package)
        {
            Vm.NotifySelectionChanged();
        }

        private void DropBox_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Install";
        }

        private async void DropBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();
            LaunchInstallers(paths);
        }

        private async void DropBox_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                foreach (var ext in InstallerExtensions) picker.FileTypeFilter.Add(ext);
                var files = await picker.PickMultipleFilesAsync();
                LaunchInstallers(files.Select(f => f.Path).ToList());
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Could not open file picker: {ex.Message}";
            }
        }

        private void LaunchInstallers(System.Collections.Generic.IList<string> paths)
        {
            var accepted = paths
                .Where(p => InstallerExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
                .ToList();
            if (accepted.Count == 0)
            {
                Vm.StatusLine = "Only .crx, .xpi, .exe and .msi files can be installed here.";
                return;
            }
            foreach (var path in accepted)
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                try
                {
                    bool isExtension = ext is ".crx" or ".xpi";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        Verb = isExtension ? "" : "runas",
                    });
                }
                catch (Exception ex)
                {
                    Vm.StatusLine = $"Could not start {System.IO.Path.GetFileName(path)}: {ex.Message}";
                    return;
                }
            }
            Vm.StatusLine = $"Started {accepted.Count} installer{(accepted.Count == 1 ? "" : "s")}; follow each prompt.";
        }
    }
}
