// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
                if (InnerNav.SelectedItem is null && InnerNav.MenuItems.Count > 0)
                    InnerNav.SelectedItem = InnerNav.MenuItems[0];
                try { await Module.RefreshUpdateCountAsync(); } catch { }
            };
        }

        public static Visibility BadgeVis(int count) =>
            count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void InnerNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
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
}
