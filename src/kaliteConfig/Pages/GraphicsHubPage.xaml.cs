// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;

namespace kaliteConfig.Pages;

/// <summary>
/// The Graphics section's tab host. It owns the top tab strip and the frame the
/// selected tab's page lives in.
///
/// Vendor tuning used to live here too, as NVIDIA / Radeon / Display tabs
/// gated on <c>GpuVendorPresenceService</c> - a tab you cannot use was hidden
/// rather than shown disabled. Those three tabs and their whole page tree are
/// gone; vendor control is reached from Overclock, which already covers both
/// vendors, so the hub is left with the two tabs that are always meaningful.
/// </summary>
public sealed partial class GraphicsHubPage : Page
{
    public GraphicsHubPage()
    {
        this.InitializeComponent();

        // Begin parked on the first usable tab instead of forcing the Drivers page,
        // which can throw during construction on some machines and take the window down.
        var first = ViewSelector.Items.OfType<SelectorBarItem>()
            .FirstOrDefault(i => i.Visibility == Visibility.Visible);
        if (first != null)
            ViewSelector.SelectedItem = first;
        else
            ContentFrame.Navigate(typeof(GpuDriversPage));
    }

    private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item) return;

        switch (item.Tag?.ToString())
        {
            case "Drivers":
                ContentFrame.Navigate(typeof(GpuDriversPage));
                break;
            case "Overclock":
                ContentFrame.Navigate(typeof(OverclockPage));
                break;
        }
    }
}