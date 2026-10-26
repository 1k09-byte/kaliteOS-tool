using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using kaliteConfig.Models;
using System;

namespace kaliteConfig.Pages
{
    public sealed partial class StartupPage : Page
    {
        private bool _loadedOnce;

        public StartupPage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                if (_loadedOnce || ViewModel.IsLoadingStartup) return;
                _loadedOnce = true;
                await ViewModel.LoadStartupCommand.ExecuteAsync(null);
            };
        }

        private static readonly SolidColorBrush TransparentRowBrush =
            new(Microsoft.UI.Colors.Transparent);

        private void StartupRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowHoverBrush");

        private void StartupRow_PointerExited(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, null);

        private void StartupRow_PointerPressed(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowPressedBrush");

        private void StartupRow_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            SetStartupRowSurface(sender, "StartupRowHoverBrush");

        private void SetStartupRowSurface(object sender, string? brushKey)
        {
            if (sender is not Border row) return;
            row.Background = brushKey is null
                ? TransparentRowBrush
                : Resources[brushKey] as Brush ?? TransparentRowBrush;
        }

        private async void DeleteStartupEntry_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not StartupEntry row) return;
            string warning = row.Kind switch
            {
                StartupEntryKind.Service =>
                    $"Delete the '{row.Name}' service? It is removed from the system; devices or apps depending on it may break. This cannot be undone here.",
                StartupEntryKind.ScheduledTask =>
                    $"Delete scheduled task '{row.Name}'? It will never run again. This cannot be undone here.",
                _ =>
                    $"Delete startup value '{row.Name}'? It will no longer launch at sign-in. This cannot be undone here.",
            };
            var dialog = new ContentDialog
            {
                Title = $"Delete {row.Name}?",
                Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.DeleteStartupEntryCommand.ExecuteAsync(row);
        }
    }
}
