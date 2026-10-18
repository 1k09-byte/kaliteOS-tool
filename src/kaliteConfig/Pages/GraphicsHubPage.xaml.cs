// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Linq;

namespace kaliteConfig.Pages;

public sealed partial class GraphicsHubPage : Page
{
    public GraphicsHubPage()
    {
        this.InitializeComponent();

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