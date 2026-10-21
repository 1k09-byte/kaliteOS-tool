using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        private void AppSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ViewModel.SearchQuery = sender.Text ?? string.Empty;
        }

        private void ExtensionsFlyout_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // The extension list is the only thing the wheel should move while this flyout is open.
            // Without this the app list behind the flyout scrolls at the same time once the list hits an end.
            e.Handled = true;
        }

        private async void ApplyExtensions_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BrowserInstallItem item) return;
            try
            {
                await ViewModel.ApplyExtensionsCommand.ExecuteAsync(item);
            }
            catch (Exception ex)
            {
                item.ErrorMessage = ex.Message;
            }
        }

        private async void CardAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BrowserInstallItem item) return;
            try
            {
                if (item.Status == BrowserInstallStatus.UpdateAvailable)
                    await ViewModel.UpdateBrowserCommand.ExecuteAsync(item);
                else
                    await ViewModel.InstallBrowserCommand.ExecuteAsync(item);
            }
            catch (Exception ex)
            {
                item.ErrorMessage = ex.Message;
                item.Status = BrowserInstallStatus.Failed;
            }
        }

        public static bool IsBusyStatus(BrowserInstallStatus status)
            => status == BrowserInstallStatus.Downloading || status == BrowserInstallStatus.Installing;

        public static Visibility BusyVisibility(BrowserInstallStatus status)
            => IsBusyStatus(status) ? Visibility.Visible : Visibility.Collapsed;

        public static Brush StatusTextBrush(BrowserInstallStatus status) => status switch
        {
            BrowserInstallStatus.Failed => Services.Theming.ThemeHelper.Brush("SystemFillColorCriticalBrush", Microsoft.UI.Colors.OrangeRed),
            BrowserInstallStatus.NotInstalled or BrowserInstallStatus.UpdateAvailable
                => Services.Theming.ThemeHelper.Brush("AccentTextFillColorPrimaryBrush", Microsoft.UI.Colors.DodgerBlue),
            _ => Services.Theming.ThemeHelper.Brush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray),
        };

        public static string ActionLabel(BrowserInstallStatus status) => status switch
        {
            BrowserInstallStatus.NotInstalled => "Install",
            BrowserInstallStatus.UpdateAvailable => "Update",
            BrowserInstallStatus.Failed => "Retry",
            BrowserInstallStatus.Downloading => "Downloading",
            BrowserInstallStatus.Installing => "Installing",
            _ => "Installed"
        };

        public static bool ActionEnabled(BrowserInstallStatus status)
            => status == BrowserInstallStatus.NotInstalled
                || status == BrowserInstallStatus.UpdateAvailable
                || status == BrowserInstallStatus.Failed;

        public static Visibility StateTextVisibility(BrowserInstallStatus status)
            => status == BrowserInstallStatus.NotInstalled ? Visibility.Collapsed : Visibility.Visible;
    }
}
