using Microsoft.UI.Xaml.Controls;
using stellarisKIT.Models;
using System;
using System.Linq;

namespace stellarisKIT.Pages
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
    }
}

