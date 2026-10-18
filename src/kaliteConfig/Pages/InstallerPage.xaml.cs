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
            ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<ViewModels.InstallerViewModel>(App.Services) ?? new InstallerViewModel(new Services.InstallerService());
            InitializeComponent();
        }

        private void MainSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            bool uninstall = sender.SelectedItem == TabUninstall;
            UninstallPanel.Visibility = uninstall ? Visibility.Visible : Visibility.Collapsed;
            InstallPanel.Visibility = uninstall ? Visibility.Collapsed : Visibility.Visible;
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
            DialogStatusText.Visibility = Visibility.Collapsed;
            DialogStatusText.Text = string.Empty;
            BrowserDetailsDialog.XamlRoot = this.XamlRoot;
            using var centerLock = Services.DialogCenterLock.Attach(BrowserDetailsDialog);
            await BrowserDetailsDialog.ShowAsync();
        }

        private async void DialogProgressBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBrowser == null) return;
            try
            {
                if (ViewModel.SelectedBrowser.Status == BrowserInstallStatus.UpdateAvailable)
                    await ViewModel.UpdateBrowserCommand.ExecuteAsync(ViewModel.SelectedBrowser);
                else
                    await ViewModel.InstallBrowserCommand.ExecuteAsync(ViewModel.SelectedBrowser);
            }
            catch (Exception ex) { ShowStatus("Install failed", ex.Message); }
        }

        public static string RowActionLabel(BrowserInstallStatus status)
            => status == BrowserInstallStatus.UpdateAvailable ? "Update" : "Install";

        public static Visibility RowActionVisibility(BrowserInstallStatus status)
            => status is BrowserInstallStatus.NotInstalled
                      or BrowserInstallStatus.UpdateAvailable
                      or BrowserInstallStatus.Failed
                ? Visibility.Visible
                : Visibility.Collapsed;

        public static bool IsBusyStatus(BrowserInstallStatus status)
            => status == BrowserInstallStatus.Downloading || status == BrowserInstallStatus.Installing;

        public static Visibility BusyVisibility(BrowserInstallStatus status)
            => IsBusyStatus(status) ? Visibility.Visible : Visibility.Collapsed;

        private async void RowAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BrowserInstallItem item) return;

            ViewModel.SelectedBrowser = item;
            btn.IsEnabled = false;
            try
            {
                if (item.Status == BrowserInstallStatus.UpdateAvailable)
                    await ViewModel.UpdateBrowserCommand.ExecuteAsync(item);
                else
                    await ViewModel.InstallBrowserCommand.ExecuteAsync(item);
            }
            catch (Exception ex)
            {
                ShowStatus("Install failed", ex.Message);
            }
            finally
            {
                btn.IsEnabled = !IsBusyStatus(item.Status);
            }
        }

        private void ShowStatus(string title, string message)
        {
            try
            {
                DialogStatusText.Text = $"{title}: {message}";
                DialogStatusText.Visibility = Visibility.Visible;
            }
            catch
            {
            }
        }

        public static Visibility IsInstalledToVisibility(BrowserInstallStatus status)
        {
            return status == BrowserInstallStatus.Installed
                || status == BrowserInstallStatus.AlreadyInstalled
                || status == BrowserInstallStatus.UpdateAvailable
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

        public static string StatusToActionLabel(BrowserInstallStatus status)
            => status == BrowserInstallStatus.UpdateAvailable ? "Update" : "Install";

        public static Visibility StatusShowsTickVisibility(BrowserInstallStatus status)
            => status == BrowserInstallStatus.UpdateAvailable ? Visibility.Collapsed : Visibility.Visible;

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
