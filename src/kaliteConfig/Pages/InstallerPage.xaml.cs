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
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Models;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class InstallerPage : Page
    {
        public InstallerViewModel ViewModel { get; }

        public InstallerPage()
        {
            ViewModel = new InstallerViewModel(new Services.InstallerService());
            InitializeComponent();
            // No manual card stagger: the previous Opacity/Translation loop could
            // strand cards at partial opacity (async, uncancelled on navigate-away,
            // and indexed against unfiltered counts). Page entrance is covered by
            // the NavigationThemeTransition in InstallerPage.xaml, so cards render
            // immediately at full opacity with their resolved states.
        }

        private void MainSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            bool packages = sender.SelectedItem == TabPackages;
            bool uninstall = sender.SelectedItem == TabUninstall;
            PackagesPanel.Visibility = packages ? Visibility.Visible : Visibility.Collapsed;
            UninstallPanel.Visibility = uninstall ? Visibility.Visible : Visibility.Collapsed;
            InstallPanel.Visibility = (!packages && !uninstall) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BrowserCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is BrowserInstallItem browser)
            {
                await ShowBrowserDetailsDialogAsync(browser);
            }
        }

        private async Task ShowBrowserDetailsDialogAsync(BrowserInstallItem browser)
        {
            ViewModel.SelectedBrowser = browser;

            // Dialog lives in Page.Resources: XamlRoot must be attached per show.
            BrowserDetailsDialog.XamlRoot = this.XamlRoot;
            await BrowserDetailsDialog.ShowAsync();
        }

        private async void DialogProgressBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBrowser != null)
            {
                await ViewModel.InstallBrowserCommand.ExecuteAsync(ViewModel.SelectedBrowser);
            }
        }

        private async void DialogUninstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBrowser != null)
            {
                await ViewModel.UninstallBrowserCommand.ExecuteAsync(ViewModel.SelectedBrowser);
            }
        }

        // Static converter methods for x:Bind in DataTemplate
        public static Visibility IsInstalledToVisibility(BrowserInstallStatus status)
        {
            return status == BrowserInstallStatus.Installed || status == BrowserInstallStatus.AlreadyInstalled
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public static Visibility BoolToVis(bool isVisible)
        {
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public static Visibility StatusToNotInstalledVisibility(BrowserInstallStatus status)
        {
            return status == BrowserInstallStatus.Installed || status == BrowserInstallStatus.AlreadyInstalled
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// Cards stay clean: no static "Install" caption while states are being
        /// checked/resolved. Status text appears only when something is actually
        /// happening (downloading / installing / failed / just finished).
        /// </summary>
        public static Visibility StatusToActiveVisibility(BrowserInstallStatus status)
        {
            return status == BrowserInstallStatus.Downloading
                || status == BrowserInstallStatus.Installing
                || status == BrowserInstallStatus.Failed
                || status == BrowserInstallStatus.Installed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public static bool Not(bool val) => !val;

        public static string GetCloseButtonText(BrowserInstallStatus status) 
        {
            return status == BrowserInstallStatus.Installed || status == BrowserInstallStatus.AlreadyInstalled ? "Close" : "Cancel";
        }
    }
}
