using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;

namespace kaliteConfig.Pages
{
    public sealed partial class PowerPlansPage : Page
    {
        public PowerPlansPage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) => {
                if (ViewModel.Schemes.Count == 0) 
                    await ViewModel.LoadSchemesAsync();
            };
        }

        private void BackToHub_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(WindowsSettingsHubPage));
        }

        private async void NewPlan_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (ViewModel.Schemes.Count == 0) return;
            var defaultBase = ViewModel.SelectedScheme
                ?? ViewModel.Schemes.FirstOrDefault(s => s.IsActive)
                ?? ViewModel.Schemes[0];

            var baseBox = new ComboBox { ItemsSource = ViewModel.Schemes, SelectedItem = defaultBase, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
            baseBox.DisplayMemberPath = "Name";
            var nameBox = new TextBox { PlaceholderText = "Plan name", Text = "Custom plan" };
            var descBox = new TextBox { PlaceholderText = "Description (optional)", AcceptsReturn = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "Base plan", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            panel.Children.Add(baseBox);
            panel.Children.Add(new TextBlock { Text = "Name", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "Description", Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            panel.Children.Add(descBox);

            var dialog = new ContentDialog
            {
                Title = "Create power plan",
                Content = panel,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (baseBox.SelectedItem is not Models.PowerScheme chosen) return;
            await ViewModel.CreatePlanAsync(chosen.Id, nameBox.Text.Trim(), descBox.Text.Trim());
        }
    }
}

