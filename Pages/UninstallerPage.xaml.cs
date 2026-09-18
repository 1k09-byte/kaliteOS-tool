using Microsoft.UI.Xaml.Controls;
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

        private void BackToHub_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(WindowsSettingsHubPage));
        }

        private void FilterChip_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton clicked) return;
            int index = 0;
            if (clicked.Tag is string s) int.TryParse(s, out index);
            else if (clicked.Tag is int i) index = i;
            ViewModel.FilterIndex = index;
            foreach (var chip in new[] { ChipAll, ChipDesktop, ChipStore, ChipNoUn })
                chip.IsChecked = chip == clicked;
        }

        private void AppList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            int n = AppList.SelectedItems.Count;
            SelectionText.Text = n == 0 ? string.Empty : $"{n} selected";
            // Batch commands run off IsSelected — mirror the ListView selection into it.
            foreach (var removed in e.RemovedItems.OfType<UninstallerItem>())
                removed.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<UninstallerItem>())
                added.IsSelected = true;
        }

        private async void DetailsUninstall_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var targets = ViewModel.Apps.Where(a => a.IsSelected).ToList();
            if (ViewModel.SelectedApp != null && targets.Count == 0) targets.Add(ViewModel.SelectedApp);
            if (targets.Count == 0) return;
            var lines = string.Join("\n", targets.Take(8).Select(a => $"• {a.Name}"));
            if (targets.Count > 8) lines += $"\n… and {targets.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Uninstall {targets.Count} application{(targets.Count == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = lines + "\n\nRuns each registered uninstaller, then cleans leftovers.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
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
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ForceRemoveCommand.ExecuteAsync(null);
        }

        private void DriverList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            // Batch removal runs off IsSelected — mirror the ListView selection into it.
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
            var lines = string.Join("\n", targets.Take(8).Select(d => $"• {d.FriendlyKind} ({d.PublishedName})"));
            if (targets.Count > 8) lines += $"\n… and {targets.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Remove {targets.Count} driver package{(targets.Count == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = lines + "\n\nDeletes each package from the driver store and uninstalls it from devices using it. Those devices may stop working until a driver is reinstalled or the PC restarts.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.RemoveDriverCommand.ExecuteAsync(null);
        }

        private bool _startupAutoloaded;

        private void MainPivot_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Controls.Pivot pivot) return;
            // The applications toolbar describes apps only — hide it on the
            // Remover/Startup tabs.
            ViewModel.IsUninstallerTabActive = pivot.SelectedIndex == 0;
            // Lazy-load the startup scan on first open: WMI + schtasks take
            // seconds, so the Uninstaller tab stays fast.
            if (pivot.SelectedIndex == 2
                && !_startupAutoloaded
                && !ViewModel.IsLoadingStartup)
            {
                _startupAutoloaded = true;
                _ = ViewModel.LoadStartupCommand.ExecuteAsync(null);
            }
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
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.DeleteStartupEntryCommand.ExecuteAsync(row);
        }
    }
}

