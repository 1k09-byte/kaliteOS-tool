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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using kaliteConfig.Models;
using System;
using System.Linq;

namespace kaliteConfig.Pages
{
    public sealed partial class UninstallerPage : Page
    {
        public UninstallerPage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) => {
                if (ViewModel.Apps.Count == 0 && !ViewModel.IsLoading)
                {
                    await ViewModel.LoadAppsCommand.ExecuteAsync(null);
                }
                if (ViewModel.DriverPackages.Count == 0 && !ViewModel.IsLoadingDrivers)
                {
                    await ViewModel.LoadDriversCommand.ExecuteAsync(null);
                }
            };
        }

        private void AppList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            int n = AppList.SelectedItems.Count;
            SelectionText.Text = n == 0 ? string.Empty : $"{n} selected";
            AppDetailsPane.IsPaneOpen = n > 0;
            foreach (var removed in e.RemovedItems.OfType<UninstallerItem>())
                removed.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<UninstallerItem>())
                added.IsSelected = true;
        }

        // Pressing the already-selected row again collapses the details card. The press is
        // sampled before ListViewItem updates its selection, and the clear runs after the
        // item's own release handling so it is not immediately re-selected.
        private UninstallerItem? _pressedApp;
        private bool _appPressWasSelected;

        private void AppRow_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pressedApp = (sender as FrameworkElement)?.DataContext as UninstallerItem;
            _appPressWasSelected = _pressedApp != null && ReferenceEquals(_pressedApp, AppList.SelectedItem);
        }

        private void AppRow_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_appPressWasSelected || _pressedApp is null || !ReferenceEquals(_pressedApp, AppList.SelectedItem)) return;
            _appPressWasSelected = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                AppList.SelectedItem = null;
                ViewModel.SelectedApp = null;
            });
        }

        // Closing the pane tears down the card that hosts the close button, so the
        // change is deferred until the click has finished dispatching.
        private void DetailsClose_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AppList.SelectedItem = null;
                ViewModel.SelectedApp = null;
            });
        }

        private async void DetailsUninstall_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var targets = ViewModel.Apps.Where(a => a.IsSelected).ToList();
            if (ViewModel.SelectedApp != null && targets.Count == 0) targets.Add(ViewModel.SelectedApp);
            if (targets.Count == 0) return;
            var lines = string.Join("\n", targets.Take(8).Select(a => $"� {a.Name}"));
            if (targets.Count > 8) lines += $"\n� and {targets.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Uninstall {targets.Count} application{(targets.Count == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = lines + "\n\nRuns each registered uninstaller, then cleans leftovers.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.UninstallSelectedCommand.ExecuteAsync(null);
        }

        private async void DetailsForceRemove_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var name = ViewModel.SelectedApp?.Name ?? "selected applications";
            var dialog = new ContentDialog
            {
                Title = "Force-remove residue?",
                Content = new TextBlock { Text = $"Scans for leftover files, folders and registry keys of {name} and deletes what it finds. The app itself is not uninstalled by this.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Force remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ForceRemoveCommand.ExecuteAsync(null);
        }

        private DriverPackageItem? _pressedDriver;
        private bool _driverPressWasSelected;

        private void DriverRow_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pressedDriver = (sender as FrameworkElement)?.DataContext as DriverPackageItem;
            _driverPressWasSelected = _pressedDriver != null && ReferenceEquals(_pressedDriver, DriverList.SelectedItem);
        }

        private void DriverRow_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_driverPressWasSelected || _pressedDriver is null || !ReferenceEquals(_pressedDriver, DriverList.SelectedItem)) return;
            _driverPressWasSelected = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                DriverList.SelectedItem = null;
                ViewModel.SelectedDriver = null;
            });
        }

        private void DetailsCloseDriver_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                DriverList.SelectedItem = null;
                ViewModel.SelectedDriver = null;
            });
        }

        private void DriverList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            DriverDetailsPane.IsPaneOpen = DriverList.SelectedItems.Count > 0;
            foreach (var removed in e.RemovedItems.OfType<DriverPackageItem>())
                removed.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<DriverPackageItem>())
                added.IsSelected = true;
        }

        private async void DetailsRemoveDriver_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var targets = ViewModel.DriverPackages.Where(d => d.IsSelected).ToList();
            if (ViewModel.SelectedDriver != null && targets.Count == 0) targets.Add(ViewModel.SelectedDriver);
            if (targets.Count == 0) return;
            var lines = string.Join("\n", targets.Take(8).Select(d => $"� {d.FriendlyKind} ({d.PublishedName})"));
            if (targets.Count > 8) lines += $"\n� and {targets.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Remove {targets.Count} driver package{(targets.Count == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = lines + "\n\nDeletes each package from the driver store and uninstalls it from devices using it. Those devices may stop working until a driver is reinstalled or the PC restarts.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.RemoveDriverCommand.ExecuteAsync(null);
        }

        private bool _startupAutoloaded;

        private void MainPivot_SelectionChanged(object sender, SelectorBarSelectionChangedEventArgs e)
        {
            if (sender is not SelectorBar bar) return;
            bool uninstaller = bar.SelectedItem == TabUninstaller;
            bool startup = bar.SelectedItem == TabStartup;

            UninstallerPanel.Visibility = uninstaller ? Visibility.Visible : Visibility.Collapsed;
            RemoverPanel.Visibility = bar.SelectedItem == TabRemover ? Visibility.Visible : Visibility.Collapsed;
            StartupPanel.Visibility = startup ? Visibility.Visible : Visibility.Collapsed;

            ViewModel.IsUninstallerTabActive = uninstaller;

            if (startup && !_startupAutoloaded && !ViewModel.IsLoadingStartup)
            {
                _startupAutoloaded = true;
                _ = ViewModel.LoadStartupCommand.ExecuteAsync(null);
            }
        }

        private static readonly SolidColorBrush TransparentRowBrush =
            new(Microsoft.UI.Colors.Transparent);

        private void StartupRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowHoverBrush");

        private void StartupRow_PointerExited(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, null);

        private void StartupRow_PointerPressed(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowPressedBrush");

        private void StartupRow_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowHoverBrush");

        private void SetStartupRowSurface(object sender, string? brushKey)
        {
            if (sender is not Border row) return;
            row.Background = brushKey is null
                ? TransparentRowBrush
                : Resources[brushKey] as Brush ?? TransparentRowBrush;
        }

        private async void DeleteStartupEntry_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if ((sender as Microsoft.UI.Xaml.FrameworkElement)?.Tag is not StartupEntry row) return;
            string warning = row.Kind switch
            {
                StartupEntryKind.Service =>
                    $"Delete the '{row.Name}' service? It is removed from the system; devices or apps depending on it may break. This cannot be undone here.",
                StartupEntryKind.ScheduledTask =>
                    $"Delete scheduled task '{row.Name}'? It will never run again. This cannot be undone here.",
                _ =>
                    $"Delete startup value '{row.Name}'? It will no longer launch at sign-in. This cannot be undone here.",
            };
            var dialog = new ContentDialog
            {
                Title = $"Delete {row.Name}?",
                Content = new TextBlock { Text = warning, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.DeleteStartupEntryCommand.ExecuteAsync(row);
        }
    }
}

