// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using System.Linq;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class SoftwareUpdatesPage : Page
    {
        public SoftwareUpdatesViewModel Vm { get; } = new();

        public SoftwareUpdatesPage()
        {
            this.InitializeComponent();
            this.Loaded += async (_, _) =>
            {
                if (Vm.Items.Count == 0 && !Vm.IsBusy)
                    await Vm.LoadCommand.ExecuteAsync(null);
            };
        }

        public static Visibility EmptyVis(int count, bool busy) =>
            count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;

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
                           $"Publisher: {OrDash(package.Publisher)}",
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
            var module = PackageManagerModule.Instance;
            var existing = module.Bundles.LoadAll();
            var nameBox = new TextBox { PlaceholderText = "Bundle name", Text = $"Updates {DateTime.Now:yyyy-MM-dd}" };
            var pickExisting = new ComboBox { PlaceholderText = "…or pick an existing bundle" };
            foreach (var b in existing) pickExisting.Items.Add(b.Name);
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(nameBox);
            panel.Children.Add(pickExisting);
            var dialog = new ContentDialog
            {
                Title = $"Add {selection.Count} package{(selection.Count == 1 ? "" : "s")} to bundle",
                Content = panel,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string name = pickExisting.SelectedItem as string ?? nameBox.Text.Trim();
            if (name.Length == 0) return;
            var updated = new System.Collections.Generic.List<BundleItem>();
            var bundle = module.Bundles.LoadAll()
                .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? new PackageBundle { Name = name };
            var known = bundle.Items.Select(i => i.SourceId + "\u0001" + i.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in selection)
            {
                string key = p.SourceId + "\u0001" + p.Id;
                if (known.Add(key))
                    bundle.Items.Add(new BundleItem { PackageId = p.Id, SourceId = p.SourceId, DisplayName = p.DisplayName });
            }
            module.Bundles.Save(bundle);
            Vm.StatusLine = $"'{bundle.Name}' now holds {bundle.Items.Count} package{(bundle.Items.Count == 1 ? "" : "s")}.";
        }

        private async void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeChoices.Add("CSV", new[] { ".csv" });
                picker.SuggestedFileName = $"package-updates-{DateTime.Now:yyyy-MM-dd}";
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await Windows.Storage.FileIO.WriteTextAsync(file, PackageCsvExporter.Build(Vm.Filtered));
                Vm.StatusLine = $"Exported {Vm.Filtered.Count} rows.";
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Export failed: {ex.Message}";
            }
        }
    }
}
