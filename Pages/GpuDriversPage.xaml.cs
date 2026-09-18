using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using kaliteConfig.Models;
using kaliteConfig.ViewModels;
using System;

namespace kaliteConfig.Pages
{
    public sealed partial class GpuDriversPage : Page
    {
        public GpuDriversViewModel ViewModel { get; }

        public GpuDriversPage()
        {
            this.InitializeComponent();
            this.ViewModel = new GpuDriversViewModel();
            this.DataContext = ViewModel;

            // Kick off hardware detection as soon as the page loads, then
            // automatically check for updates so the card shows current vs
            // latest without a manual click.
            this.Loaded += async (_, _) =>
            {
                await ViewModel.DetectGpusCommand.ExecuteAsync(null);
                await ViewModel.CheckDriverCommand.ExecuteAsync(ViewModel.SelectedDriver);
            };
        }

        // The overclock section re-detects with the rest of the page: a fresh
        // GPU handle invalidates the controller's cached adapter, so the curve
        // loop must hand fan control back to the driver first (spec §5).
        private async void RedetectBtn_Click(object sender, RoutedEventArgs e)
        {
            try { await OverclockPanel.Vm.RefreshAsync(); }
            catch { /* re-detect must never break the drivers page */ }
        }

        private async void CheckForUpdatesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is GpuDriverItem driver)
            {
                await ViewModel.CheckDriverCommand.ExecuteAsync(driver);
            }
        }

        private async void DownloadInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is GpuDriverItem driver)
            {
                await ViewModel.InstallDriverCommand.ExecuteAsync(driver);
            }
        }

        public static Visibility NullToVis(GpuDriverItem driver)
        {
            return driver != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public static Visibility PrimaryVis(bool isPrimary) => isPrimary ? Visibility.Visible : Visibility.Collapsed;
        public static Visibility SecondaryVis(bool isPrimary) => isPrimary ? Visibility.Collapsed : Visibility.Visible;

        public static bool IsInstallButtonEnabled(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.UpdateAvailable or GpuDriverStatus.NotInstalled or GpuDriverStatus.Failed;
        }

        // One action button per card: install when a driver is ready to install,
        // otherwise a plain "Check for updates".
        public static Visibility InstallBtnVis(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.UpdateAvailable or GpuDriverStatus.NotInstalled or GpuDriverStatus.Failed
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public static Visibility CheckBtnVis(GpuDriverStatus status)
        {
            return InstallBtnVis(status) == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        // Progress block: visible while downloading or installing.
        public static Visibility ProgressVis(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.Downloading or GpuDriverStatus.Installing
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // Indeterminate bar during the install phase (no percentage available);
        // during download it's a determinate bar fed by DownloadProgress.
        public static bool InstallPhaseVis(GpuDriverStatus status)
        {
            return status == GpuDriverStatus.Installing;
        }

        public static Visibility ErrorVis(string errorMessage)
        {
            return string.IsNullOrEmpty(errorMessage) ? Visibility.Collapsed : Visibility.Visible;
        }

        public static BitmapImage VendorIcon(string vendor)
        {
            string path = string.Empty;
            if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/nvidia-logo.png";
            else if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/amd-logo.png";
            else if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                path = "ms-appx:///Assets/intel-logo.png";

            return string.IsNullOrEmpty(path) ? new BitmapImage() : new BitmapImage(new Uri(path));
        }
    }
}
