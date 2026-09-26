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

        private void ResultsList_InfoRequested(object sender, PackageInfo package)
        {
            var dialog = new ContentDialog
            {
                Title = package.DisplayName,
                Content = new TextBlock
                {
                    Text = $"ID: {package.Id}\n" +
                           $"Installed: {OrDash(package.InstalledVersion)}\n" +
                           $"Available: {OrDash(package.AvailableVersion)}\n" +
                           $"Source: {package.SourceLabel}\n" +
                           $"Publisher: {OrDash(package.Publisher)}\n\n" +
                           (string.IsNullOrWhiteSpace(package.Description) ? "No description." : package.Description),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            _ = dialog.ShowAsync();
        }

        private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();

        private void ResultsList_SelectionToggled(object sender, PackageInfo package)
        {
            Vm.NotifySelectionChanged();
        }

        private async void AddToBundle_Click(object sender, RoutedEventArgs e)
        {
            var selection = Vm.Filtered.Where(p => p.IsSelected).ToList();
            if (selection.Count == 0)
            {
                Vm.StatusLine = "Nothing selected.";
                return;
            }
            var nameBox = new TextBox { PlaceholderText = "Bundle name", Text = $"Bundle {DateTime.Now:yyyy-MM-dd}" };
            var dialog = new ContentDialog
            {
                Title = $"Add {selection.Count} package{(selection.Count == 1 ? "" : "s")} to bundle",
                Content = nameBox,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string name = nameBox.Text.Trim();
            if (name.Length == 0) return;
            try
            {
                var bundle = Vm.AddToBundle(selection, name);
                Vm.StatusLine = $"'{bundle.Name}' now holds {bundle.Items.Count} package{(bundle.Items.Count == 1 ? "" : "s")}.";
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Could not save bundle: {ex.Message}";
            }
        }

        private async void ManualInstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".msi");
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file.Path,
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Vm.StatusLine = $"Launched {file.Name} (follow its own installer UI).";
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Manual install failed: {ex.Message}";
            }
        }
    }
}
