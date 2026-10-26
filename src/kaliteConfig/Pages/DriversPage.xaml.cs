using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using kaliteConfig.Models;
using System;
using System.Linq;

namespace kaliteConfig.Pages
{
    public sealed partial class DriversPage : Page
    {
        public DriversPage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                if (ViewModel.DriverPackages.Count == 0 && !ViewModel.IsLoadingDrivers)
                {
                    await ViewModel.LoadDriversCommand.ExecuteAsync(null);
                }
            };
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

        private void DetailsCloseDriver_Click(object sender, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                DriverList.SelectedItem = null;
                ViewModel.SelectedDriver = null;
            });
        }

        private void DriverList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DriverDetailsPane.IsPaneOpen = DriverList.SelectedItems.Count > 0;
            foreach (var removed in e.RemovedItems.OfType<DriverPackageItem>())
                removed.IsSelected = false;
            foreach (var added in e.AddedItems.OfType<DriverPackageItem>())
                added.IsSelected = true;
        }

        private async void DetailsRemoveDriver_Click(object sender, RoutedEventArgs e)
        {
            var targets = ViewModel.DriverPackages.Where(d => d.IsSelected).ToList();
            if (ViewModel.SelectedDriver != null && targets.Count == 0) targets.Add(ViewModel.SelectedDriver);
            if (targets.Count == 0) return;
            var lines = string.Join("\n", targets.Take(8).Select(d => $"- {d.FriendlyKind} ({d.PublishedName})"));
            if (targets.Count > 8) lines += $"\n- and {targets.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Remove {targets.Count} driver package{(targets.Count == 1 ? "" : "s")}?",
                Content = new TextBlock { Text = lines + "\n\nDeletes each package from the driver store and uninstalls it from devices using it. Those devices may stop working until a driver is reinstalled or the PC restarts.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.RemoveDriverCommand.ExecuteAsync(null);
        }
    }
}
