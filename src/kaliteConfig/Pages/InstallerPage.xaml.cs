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

            // Restore the saved layout after InitializeComponent, so the initial
            // pass below paints the right mode on the very first frame rather
            // than flashing cards and then swapping.
            ViewModel.LoadViewMode();
            ApplyViewMode();
        }

        /// <summary>
        /// Shows exactly one of the two layouts per section.
        ///
        /// Each section has two ItemsRepeaters over the SAME collection, so
        /// switching is a visibility flip rather than a re-query: the filter
        /// state, install progress and selection all live on the shared
        /// BrowserInstallItem instances and stay intact.
        /// </summary>
        private void ApplyViewMode()
        {
            var mode = ViewModel.ViewMode;

            foreach (var (grid, list) in new (FrameworkElement, FrameworkElement)[]
            {
                (BrowserGrid, BrowserList),
                (LauncherGrid, LauncherList),
                (SocialGrid, SocialList),
                (UtilityGrid, UtilityList),
            })
            {
                grid.Visibility = mode == AppViewMode.Card ? Visibility.Visible : Visibility.Collapsed;
                list.Visibility = mode == AppViewMode.Compact ? Visibility.Visible : Visibility.Collapsed;
            }

            // Keep the radio buttons in step when the mode is set from code
            // (on load) rather than by a click.
            ViewCardBtn.IsChecked = mode == AppViewMode.Card;
            ViewCompactBtn.IsChecked = mode == AppViewMode.Compact;
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            // A RadioButton in a group is already mutually exclusive, so the
            // sender's identity is enough to decide the mode.
            ViewModel.ViewMode = ReferenceEquals(sender, ViewCardBtn) ? AppViewMode.Card : AppViewMode.Compact;
            ApplyViewMode();
        }

        private void MainSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            // The Packages tab was removed at the user's request, so this now
            // only flips between Install and Uninstall.
            bool uninstall = sender.SelectedItem == TabUninstall;
            UninstallPanel.Visibility = uninstall ? Visibility.Visible : Visibility.Collapsed;
            InstallPanel.Visibility = uninstall ? Visibility.Collapsed : Visibility.Visible;

            // The Cards/Compact switcher only re-lays-out the Install tab's app
            // grid, so it is shown there and nowhere else.
            ViewModePanel.Visibility = uninstall ? Visibility.Collapsed : Visibility.Visible;
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

        // async void: anything that escapes is an unhandled UI exception and
        // kills the app. An install/uninstall that fails (network drop, refused
        // file lock, missing winget) has to surface as a status line instead.
        private async void DialogProgressBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBrowser == null) return;
            try { await ViewModel.InstallBrowserCommand.ExecuteAsync(ViewModel.SelectedBrowser); }
            catch (Exception ex) { ShowStatus("Install failed", ex.Message); }
        }

        // DialogUninstallBtn_Click was removed with the dialog's Uninstall
        // button. Uninstalling an installed app now happens only on the
        // Uninstall tab (UninstallerPage), so the Apps details dialog is
        // install-only and UninstallBrowserCommand is reached from elsewhere.

        /// <summary>Reports a failed install/uninstall inside the open dialog,
        /// so the reason is visible instead of lost to an unhandled exception.</summary>
        private void ShowStatus(string title, string message)
        {
            try
            {
                BrowserDetailsDialog.Content = new TextBlock
                {
                    Text = $"{title}: {message}",
                    TextWrapping = TextWrapping.Wrap,
                };
                BrowserDetailsDialog.CloseButtonText = "Close";
            }
            catch
            {
                // Never let the error path throw as well.
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
