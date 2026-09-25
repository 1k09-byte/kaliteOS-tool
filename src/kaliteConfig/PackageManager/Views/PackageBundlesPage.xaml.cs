using System;
using System.IO;
using System.Linq;
using kaliteConfig.PackageManager.Services;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class PackageBundlesPage : Page
    {
        public PackageBundlesViewModel Vm { get; } = new();

        public PackageBundlesPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => Vm.RefreshCommand.Execute(null);
        }

        private async void CheckMissing_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BundleRow row || row.IsBusy) return;
            var missing = await Vm.CheckMissingAsync(row, default);
            Vm.StatusLine = missing.Count == 0
                ? $"'{row.Bundle.Name}': everything installed."
                : $"'{row.Bundle.Name}': {missing.Count} missing.";
        }

        private async void InstallMissing_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BundleRow row || row.IsBusy) return;
            var missing = await Vm.CheckMissingAsync(row, default);
            if (missing.Count == 0)
            {
                Vm.StatusLine = $"'{row.Bundle.Name}': everything installed.";
                return;
            }
            var outcomes = await Vm.InstallMissingAsync(row, missing, default);
            int ok = outcomes.Count(o => o.Result.Success);
            int failed = outcomes.Count - ok;
            Vm.StatusLine = failed == 0
                ? $"Installed {ok} package{(ok == 1 ? "" : "s")} from '{row.Bundle.Name}'."
                : $"{ok} installed, {failed} failed: " + string.Join("; ",
                    outcomes.Where(o => !o.Result.Success).Take(3).Select(o => $"{o.Item.DisplayName}: {o.Result.Message}"));
        }

        private async void ExportBundle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BundleRow row) return;
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeChoices.Add("JSON bundle", new[] { ".json" });
                picker.SuggestedFileName = row.Bundle.Name;
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await Windows.Storage.FileIO.WriteTextAsync(file, PackageManagerModule.Instance.Bundles.ExportToJson(row.Bundle));
                Vm.StatusLine = $"Exported '{row.Bundle.Name}'.";
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Export failed: {ex.Message}";
            }
        }

        private async void DeleteBundle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BundleRow row) return;
            var dialog = new ContentDialog
            {
                Title = $"Delete bundle '{row.Bundle.Name}'?",
                Content = new TextBlock { Text = "Removes the saved list. Installed packages are untouched.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            Vm.DeleteBundleCommand.Execute(row);
        }

        private async void NewBundle_Click(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox { PlaceholderText = "Bundle name" };
            var dialog = new ContentDialog
            {
                Title = "New bundle",
                Content = nameBox,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (nameBox.Text.Trim().Length == 0) return;
            Vm.Create(nameBox.Text.Trim(), Array.Empty<Models.PackageInfo>());
            Vm.StatusLine = $"Created '{nameBox.Text.Trim()}'. Add packages to it from any list.";
        }

        private async void ImportBundle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                if (App.MainWindow != null)
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                var bundle = PackageManagerModule.Instance.Bundles.ImportFromJson(json);
                if (bundle is null)
                {
                    Vm.StatusLine = "That file is not a package bundle.";
                    return;
                }
                PackageManagerModule.Instance.Bundles.Save(bundle);
                Vm.RefreshCommand.Execute(null);
                Vm.StatusLine = $"Imported '{bundle.Name}' ({bundle.Items.Count} packages).";
            }
            catch (Exception ex)
            {
                Vm.StatusLine = $"Import failed: {ex.Message}";
            }
        }
    }
}
