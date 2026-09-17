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

            // Kick off hardware detection as soon as the page loads.
            this.Loaded += async (_, _) => await ViewModel.DetectGpusCommand.ExecuteAsync(null);
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

        public static Visibility ProgressVis(GpuDriverStatus status)
        {
            return status is GpuDriverStatus.Downloading or GpuDriverStatus.Installing ? Visibility.Visible : Visibility.Collapsed;
        }

        public static bool InstallPhaseVis(GpuDriverStatus status)
        {
            return status == GpuDriverStatus.Installing;
        }

        public static Visibility ErrorVis(string? errorMessage)
        {
            return string.IsNullOrWhiteSpace(errorMessage) ? Visibility.Collapsed : Visibility.Visible;
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
