using System;
using System.Linq;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class InstalledPackagesPage : Page
    {
        public InstalledPackagesViewModel Vm { get; } = new();

        public InstalledPackagesPage()
        {
            this.InitializeComponent();
            this.Loaded += async (_, _) =>
            {
                if (Vm.Items.Count == 0 && !Vm.IsBusy)
                    await Vm.LoadCommand.ExecuteAsync(null);
            };
        }

        public static Visibility EmptyVis(int count, bool busy) =>
            count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag && int.TryParse(tag, out int mode))
                Vm.ViewMode = (PackageViewMode)mode;
        }

        private void ResultsList_InfoRequested(object sender, PackageInfo package)
        {
            var dialog = new ContentDialog
            {
                Title = package.DisplayName,
                Content = new TextBlock
                {
                    Text = $"ID: {package.Id}\n" +
                           $"Installed: {OrDash(package.InstalledVersion)}\n" +
                           $"Source: {package.SourceLabel}\n" +
                           $"Publisher: {OrDash(package.Publisher)}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            _ = dialog.ShowAsync();
        }

        private static string OrDash(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();

        private void ResultsList_SelectionToggled(object sender, PackageInfo package)
        {
            Vm.NotifySelectionChanged();
        }

        /// <summary>
        /// Single-row uninstall always confirms first (safety section): the
        /// row has no direct command on purpose.
        /// </summary>
        private async void ResultsList_ActionRequested(object sender, PackageInfo package)
        {
            var dialog = new ContentDialog
            {
                Title = $"Uninstall {package.DisplayName}?",
                Content = new TextBlock
                {
                    Text = $"{package.DisplayName} ({package.Id}) will be uninstalled from {package.SourceLabel}.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await Vm.UninstallOneCommand.ExecuteAsync(package);
        }

        private async void UninstallSelection_Click(object sender, RoutedEventArgs e)
        {
            var selection = Vm.Filtered.Where(p => p.IsSelected).ToList();
            if (selection.Count == 0)
            {
                Vm.StatusLine = "Nothing selected.";
                return;
            }
            var lines = string.Join("\n", selection.Take(8).Select(p => $"• {p.DisplayName} ({p.Id})"));
            if (selection.Count > 8) lines += $"\n… and {selection.Count - 8} more";
            var dialog = new ContentDialog
            {
                Title = $"Uninstall {selection.Count} package{(selection.Count == 1 ? "" : "s")}?",
                Content = new TextBlock
                {
                    Text = lines + "\n\nEach package is uninstalled in turn; failures are reported per item and never abort the rest.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await Vm.UninstallSelectedCommand.ExecuteAsync(null);
        }

        private async void AddToBundle_Click(object sender, RoutedEventArgs e)
        {
            var selection = Vm.Filtered.Where(p => p.IsSelected).ToList();
            if (selection.Count == 0)
            {
                Vm.StatusLine = "Nothing selected.";
                return;
            }
            var module = PackageManagerModule.Instance;
            var nameBox = new TextBox { PlaceholderText = "Bundle name", Text = $"Installed {DateTime.Now:yyyy-MM-dd}" };
            var dialog = new ContentDialog
            {
                Title = $"Add {selection.Count} package{(selection.Count == 1 ? "" : "s")} to bundle",
                Content = nameBox,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string name = nameBox.Text.Trim();
            if (name.Length == 0) return;
            var bundle = module.Bundles.LoadAll()
                .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? new PackageBundle { Name = name };
            var known = bundle.Items.Select(i => i.SourceId + "\u0001" + i.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in selection)
            {
                string key = p.SourceId + "\u0001" + p.Id;
                if (known.Add(key))
                    bundle.Items.Add(new BundleItem { PackageId = p.Id, SourceId = p.SourceId, DisplayName = p.DisplayName });
            }
            module.Bundles.Save(bundle);
            Vm.StatusLine = $"'{bundle.Name}' now holds {bundle.Items.Count} package{(bundle.Items.Count == 1 ? "" : "s")}.";
        }
    }
}
