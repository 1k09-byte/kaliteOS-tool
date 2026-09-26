// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            
            var templates = new System.Collections.Generic.List<Models.PowerScheme>();
            templates.Add(new Models.PowerScheme { Id = Services.PowerService.BalancedGuid, Name = "Balanced (Windows Default)" });
            templates.Add(new Models.PowerScheme { Id = Services.PowerService.HighPerformanceGuid, Name = "High Performance" });
            templates.Add(new Models.PowerScheme { Id = Services.PowerService.UltimatePerformanceGuid, Name = "Ultimate Performance" });
            templates.Add(new Models.PowerScheme { Id = Services.PowerService.PowerSaverGuid, Name = "Power Saver" });

            foreach(var s in ViewModel.Schemes) {
                if (!Services.PowerService.IsBuiltInScheme(s.Id)) templates.Add(s);
            }

            var defaultBase = templates.FirstOrDefault(s => ViewModel.SelectedScheme != null && s.Id == ViewModel.SelectedScheme.Id)
                ?? templates[0];

            var baseBox = new ComboBox { ItemsSource = templates, SelectedItem = defaultBase, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
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

