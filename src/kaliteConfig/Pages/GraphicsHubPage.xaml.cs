// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

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

        // The "Display" tab is backed by NVAPI (NVIDIA's display API). On a
        // machine without an NVIDIA chip it can only ever fail with
        // "no displays found", so don't offer it at all.
        var displayTab = ViewSelector.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, "Display", StringComparison.Ordinal));
        if (displayTab is not null)
            displayTab.Visibility = vendors.HasNvidia ? Visibility.Visible : Visibility.Collapsed;

        if (!vendors.HasNvidia && ViewSelector.SelectedItem is NavigationViewItem sel &&
            string.Equals(sel.Tag as string, "Display", StringComparison.Ordinal))
        {
            var first = ViewSelector.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible);
            if (first is not null)
                ViewSelector.SelectedItem = first;
        }
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
                // Hosts both the simple and the flat 3D view, so Simple Driver
                // Settings lives inside the NVIDIA section rather than beside it.
                ContentFrame.Navigate(typeof(NvidiaSectionPage));
                break;
            case "Radeon":
                ContentFrame.Navigate(typeof(RadeonSettingsPage));
                break;
            case "Display":
                ContentFrame.Navigate(typeof(NvidiaSettingsPage));
                break;
        }
    }
}
