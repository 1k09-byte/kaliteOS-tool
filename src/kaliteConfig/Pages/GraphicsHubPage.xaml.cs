// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.Pages;

public sealed partial class GraphicsHubPage : Page
{
    public GraphicsHubPage()
    {
        this.InitializeComponent();

        ApplyVendorTabs();
        ContentFrame.Navigate(typeof(GpuDriversPage));
    }

    /// <summary>
    /// Shows only the vendor tabs this machine can actually use.
    ///
    /// The alternative - always showing both - is worse than it sounds: a
    /// Radeon tab on a machine with no AMD GPU opens an empty page of disabled
    /// rows, and an NVIDIA tab on a machine with no NVIDIA card opens one full
    /// of "NVAPI is not available". A tab that cannot do anything should not be
    /// there.
    ///
    /// Detection reads the display-class registry key, which is a few hundred
    /// microseconds; WMI (what the Drivers tab uses for the full picture) takes
    /// hundreds of milliseconds and would stall the page on open.
    /// </summary>
    private void ApplyVendorTabs()
    {
        GpuVendorFlags vendors = GpuVendorPresenceService.Detect();
        NvidiaTab.Visibility = vendors.HasNvidia ? Visibility.Visible : Visibility.Collapsed;
        RadeonTab.Visibility = vendors.HasAmd ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewSelector_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem navItem) return;

        switch (navItem.Tag?.ToString())
        {
            case "Drivers":
                ContentFrame.Navigate(typeof(GpuDriversPage));
                break;
            case "Overclock":
                ContentFrame.Navigate(typeof(OverclockPage));
                break;
            case "NVIDIA":
                ContentFrame.Navigate(typeof(NvidiaSettingsPage));
                break;
            case "Radeon":
                ContentFrame.Navigate(typeof(RadeonSettingsPage));
                break;
        }
    }
}
