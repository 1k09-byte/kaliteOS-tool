using System;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class PackageManagersPage : Page
    {
        public PackageManagerPreferencesViewModel Vm { get; } = new();

        public PackageManagersPage()
        {
            this.InitializeComponent();
            this.Loaded += async (_, _) =>
            {
                if (Vm.Rows.Count == 0 && !Vm.IsBusy)
                    await Vm.RefreshCommand.ExecuteAsync(null);
            };
        }

        private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PackageSourceStatus row
                && sender is ToggleSwitch toggle
                && row.IsEnabled != toggle.IsOn)
            {
                Vm.SetEnabled(row, toggle.IsOn);
            }
        }

        private async void Executable_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not PackageSourceStatus row) return;
            var module = PackageManagerModule.Instance;
            var pathBox = new TextBox
            {
                PlaceholderText = @"e.g. C:\tools\winget.exe (blank = auto-detect)",
                Text = module.Sources.GetExecutableOverride(row.SourceId),
            };
            var dialog = new ContentDialog
            {
                Title = $"{row.DisplayName} executable",
                Content = pathBox,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                Vm.SetExecutableOverride(row.SourceId, pathBox.Text);
                Vm.StatusLine = "Override saved — refreshing detection…";
                await Vm.RefreshCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Could not save override: {ex.Message}";
            }
        }
    }
}
