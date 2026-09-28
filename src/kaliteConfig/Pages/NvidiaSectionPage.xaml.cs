// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of this software, in any form, whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.Pages;

/// <summary>
/// Groups the NVIDIA settings pages under one NVIDIA tab. Simple Driver Settings is
/// the default view because it is the one most people can use; the flat 3D list stays
/// available for anyone who prefers it.
///
/// Keeping both behind a single tab matters for discoverability: two sibling top-level
/// tabs both about "NVIDIA" read as two different features, not two views of one thing.
/// </summary>
public sealed partial class NvidiaSectionPage : Page
{
    public NvidiaSectionPage()
    {
        InitializeComponent();

        // "Simple settings" is pre-selected in the XAML, so the first selection-changed
        // may already have run; navigate here rather than relying on that.
        SectionFrame.Navigate(typeof(SimpleDriverSettingsPage));
    }

    private void SectionSelector_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem navItem) return;

        // The pre-selected item raises this during InitializeComponent, so the frame
        // may already be on the target. Navigating again would build a second copy of
        // the page and restart its driver read for no reason.
        System.Type? target = navItem.Tag?.ToString() switch
        {
            "Simple" => typeof(SimpleDriverSettingsPage),
            "Advanced3D" => typeof(NvidiaSettings3DPage),
            _ => null,
        };
        if (target is null) return;
        if (SectionFrame.CurrentSourcePageType == target) return;
        SectionFrame.Navigate(target);
    }
}
