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
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class AffinityPage : Page
    {
        public AffinityViewModel ViewModel { get; }

        public AffinityPage()
        {
            ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<AffinityViewModel>(App.Services) ?? new AffinityViewModel();
            InitializeComponent();
            Loaded += AffinityPage_Loaded;
        }

        private async void AffinityPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsRefreshing)
            {
                await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        private async void DeviceRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not AffinityDeviceItem device) return;
            await OpenDeviceAsync(device);
        }

        private async void DeviceRow_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is AffinityDeviceItem device)
                await OpenDeviceAsync(device);
        }

        private async Task OpenDeviceAsync(AffinityDeviceItem device)
        {
            try
            {
                await ViewModel.LoadDeviceDetailsAsync(device);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Device not available", ex.Message);
                return;
            }

            DeviceDetailsDialog.XamlRoot = XamlRoot;
            await DeviceDetailsDialog.ShowAsync();
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            }.ShowAsync();
        }

        private async void DialogApply_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedDevice is null)
            {
                DeviceDetailsDialog.Hide();
                return;
            }

            if (!ViewModel.IsDevicePresent(ViewModel.SelectedDevice))
            {
                string goneName = ViewModel.SelectedDevice.Name;
                DeviceDetailsDialog.Hide();
                await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
                await ShowMessageAsync("Device not present",
                    $"{goneName} is no longer present, so nothing was written. The device list has been rescanned - this is expected after a GPU restart or driver install.");
                return;
            }

            int previousChanges = ViewModel.TrackedChanges.Count;
            ViewModel.ApplyDeviceChanges(ViewModel.SelectedDevice);
            DeviceDetailsDialog.Hide();

            if (ViewModel.TrackedChanges.Count > previousChanges)
            {
                await ViewModel.RestartDeviceAfterApplyAsync(ViewModel.SelectedDevice);
            }
        }

        private void DialogCancel_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.DiscardDialogChanges();
            DeviceDetailsDialog.Hide();
        }

        private sealed class ChangeListRow
        {
            public ChangeListRow(string device, string detail)
            {
                Device = device;
                Detail = detail;
            }

            public string Device { get; }
            public string Detail { get; }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = $"Tracked Changes ({ViewModel.TrackedChanges.Count})",
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };
            if (ViewModel.TrackedChanges.Count == 0)
            {
                dialog.Content = new TextBlock
                {
                    Text = "No changes have been made yet.",
                    TextWrapping = TextWrapping.Wrap
                };
            }
            else
            {
                var rows = new System.Collections.Generic.List<ChangeListRow>();
                foreach (var c in ViewModel.TrackedChanges)
                {
                    string oldVal = FormatValue(c.PropertyName, c.OldValue);
                    string newVal = FormatValue(c.PropertyName, c.NewValue);
                    rows.Add(new ChangeListRow(c.DeviceName, $"{c.PropertyName}: {oldVal} → {newVal}"));
                }
                dialog.Content = new ListView
                {
                    ItemsSource = rows,
                    ItemTemplate = (DataTemplate)Resources["ChangeRowTemplate"],
                    ItemContainerStyle = (Style)Resources["AffinityRowContainerStyle"],
                    SelectionMode = ListViewSelectionMode.None
                };
            }
            await dialog.ShowAsync();
        }

        private static string FormatValue(string propertyName, object? value)
        {
            if (value is null) return "-";
            return propertyName switch
            {
                "MsiEnabled" => (bool)value ? "On" : "Off",
                "MessageNumberLimit" => value is int i ? (i == 0 ? "Auto" : i.ToString()) : "-",
                "DevicePolicy" => Services.AffinityService.DevicePolicyShort((int?)value),
                "DevicePriority" => Services.AffinityService.DevicePriorityName((int?)value),
                "AffinityMask" => Services.AffinityService.AffinityMaskText((ulong?)value),
                _ => value.ToString() ?? "-"
            };
        }

        public static Visibility BoolToVis(bool isVisible)
        {
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public static string ExpanderGlyph(bool expanded)
        {
            return expanded ? "\uE70D" : "\uE76C";
        }

        public static BitmapImage? VendorIcon(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string? path = null;
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/nvidia-logo.png";
            else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/amd-logo.png";
            else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/intel-logo.png";
            return path is null ? null : new BitmapImage(new Uri(path));
        }

        public static Visibility NonEmptyToVisibility(int count)
        {
            return count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public static bool Not(bool val) => !val;

        public static string CountText(int count) => count.ToString();
    }
}
