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
            ViewModel = new AffinityViewModel();
            InitializeComponent();
            Loaded += AffinityPage_Loaded;
        }

        private async void AffinityPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Rescan on EVERY visit, not just the first one. The page is cached
            // (NavigationCacheMode=Enabled), so a GPU that was restarted, or a
            // driver just installed, used to leave the table showing adapters
            // that no longer exist in that shape.
            if (!ViewModel.IsRefreshing)
            {
                await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
            }
        }

        private async void DeviceRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not AffinityDeviceItem device) return;

            try
            {
                await ViewModel.LoadDeviceDetailsAsync(device);
            }
            catch (Exception ex)
            {
                // A stale row (device restarted / re-enumerated) must not open an
                // empty dialog: report it, rescan, and let the user pick again.
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

            // Never write interrupt settings into a device that is no longer
            // there (the classic post-GPU-restart case).
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
                
            // If the application engine actually pushed a modification to the registry
            if (ViewModel.TrackedChanges.Count > previousChanges)
            {
                var restartDialog = new ContentDialog
                {
                    Title = "Restart Device",
                    Content = $"Changes have been successfully applied to {ViewModel.SelectedDevice.Name}.\n\nWould you like to restart the device now for the changes to take effect?",
                    PrimaryButtonText = "Restart Device",
                    CloseButtonText = "Later",
                    XamlRoot = XamlRoot,
                    Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
                };

                var result = await restartDialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await kaliteConfig.Services.AffinityService.RestartDeviceAsync(ViewModel.SelectedDevice.DeviceInstanceId);

                    // A restart re-enumerates the adapter: rescan so the table and
                    // the selection reflect the device that came back.
                    await ViewModel.RefreshDevicesCommand.ExecuteAsync(null);
                }
            }
        }

        private void DialogCancel_Click(object sender, RoutedEventArgs e)
        {
            // The dialog edits the row item in place - throw those staged
            // edits away so Cancel truly cancels.
            ViewModel.DiscardDialogChanges();
            DeviceDetailsDialog.Hide();
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            // Build human-readable change list for the dialog
            string content;
            if (ViewModel.TrackedChanges.Count == 0)
            {
                content = "No changes have been made yet.";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in ViewModel.TrackedChanges)
                {
                    string oldVal = FormatValue(c.PropertyName, c.OldValue);
                    string newVal = FormatValue(c.PropertyName, c.NewValue);
                    sb.AppendLine($"• {c.DeviceName}");
                    sb.AppendLine($"    {c.PropertyName}: {oldVal} → {newVal}");
                    sb.AppendLine();
                }
                content = sb.ToString().TrimEnd();
            }

            var dialog = new ContentDialog
            {
                Title = $"Tracked Changes ({ViewModel.TrackedChanges.Count})",
                Content = new ScrollViewer
                {
                    MaxHeight = 400,
                    Content = new TextBlock
                    {
                        Text = content,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };
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

        // Static converter methods for x:Bind
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
    }
}
