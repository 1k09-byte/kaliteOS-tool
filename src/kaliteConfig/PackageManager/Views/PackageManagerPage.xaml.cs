// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class PackageManagerPage : Page
    {
        public PackageManagerModule Module => PackageManagerModule.Instance;

        public PackageManagerPage()
        {
            this.InitializeComponent();
            this.Loaded += async (_, _) =>
            {
                if (SectionBar.SelectedItem is null)
                    SectionBar.SelectedItem = SectionDiscover;
                try { await Module.RefreshUpdateCountAsync(); } catch { }
            };
        }

        public static Visibility BadgeVis(int count) =>
            count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem is SelectorBarItem item && item.Tag is string tag)
            {
                NavigateToTag(tag);
            }
        }

        private void NavigateToTag(string tag)
        {
            Type? page = tag switch
            {
                "Discover" => typeof(DiscoverPackagesPage),
                "Updates" => typeof(SoftwareUpdatesPage),
                "Installed" => typeof(InstalledPackagesPage),
                "Bundles" => typeof(PackageBundlesPage),
                "Managers" => typeof(PackageManagersPage),
                _ => null,
            };
            if (page != null) ContentFrame.Navigate(page);
        }
    }
}
