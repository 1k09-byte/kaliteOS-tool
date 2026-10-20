using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using kaliteConfig.Models;
using kaliteConfig.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.Pages
{
    public sealed partial class BiosManagerPage : Page
    {
        public BiosManagerViewModel ViewModel { get; }

        public BiosManagerPage()
        {
            ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<BiosManagerViewModel>(App.Services) ?? new();
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = ViewModel.AutoLoadAsync();
        }

        private void LoadDump_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadFileCommand.Execute(null);
        }

        private async void ApplyToBios_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasDocument) return;

            var changes = ViewModel.GetChanges();
            if (changes.Count == 0)
            {
                await ShowMessageAsync("Nothing to apply", "No settings have been modified. Change a value first.");
                return;
            }

            var problems = ViewModel.ValidateChanges();
            var panel = new StackPanel { Spacing = 10, MinWidth = 460 };

            if (problems.Count > 0)
            {
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    Severity = InfoBarSeverity.Error,
                    Title = $"{problems.Count} value(s) would be invalid",
                    Message = "Revert or fix the highlighted values before applying.",
                    IsClosable = false,
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = $"{changes.Count} setting(s) will be written directly to the BIOS:",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            });
            panel.Children.Add(new ListView
            {
                ItemsSource = changes,
                ItemTemplate = (DataTemplate)Resources["ChangeSummaryTemplate"],
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 380,
            });

            var dialog = new ContentDialog
            {
                Title = "Review changes",
                Content = panel,
                PrimaryButtonText = $"Apply {changes.Count}…",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = problems.Count == 0,
                XamlRoot = XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.ApplyToBiosCommand.ExecuteAsync(null);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SaveFileCommand.Execute(null);
        }

        private void SettingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedRow = e.AddedItems?.OfType<BiosSettingRow>().FirstOrDefault();
            if (ViewModel.SelectedRow is { } row && row.IsEnumerated)
                ValueCombo.SelectedValue = row.SelectedToken;
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ViewModel.FilterCommand.Execute(sender.Text);
        }

        private void ValueCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel.SelectedRow is not { } row || !row.IsEnumerated) return;
            if (ValueCombo.SelectedItem is BiosOption option)
                row.SelectedToken = option.RawToken;
        }

        private void ValuePill_Click(object sender, RoutedEventArgs e)
        {
            if (this.ActualWidth < 1000 &&
                ((FrameworkElement)sender).DataContext is BiosSettingRow row)
            {
                _ = ShowQuickEditAsync(row);
            }
        }

        private async Task ShowQuickEditAsync(BiosSettingRow row)
        {
            Control editor;
            if (row.IsEnumerated)
            {
                editor = new ComboBox
                {
                    ItemsSource = row.Options,
                    SelectedValuePath = "RawToken",
                    DisplayMemberPath = "Display",
                    SelectedValue = row.Item.Value,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinHeight = 34,
                };
            }
            else
            {
                editor = new TextBox
                {
                    Text = row.ValueText,
                    PlaceholderText = "0x-prefixed hex or decimal",
                };
            }

            var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
            panel.Children.Add(new TextBlock
            {
                Text = row.SectionText,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(editor);

            var dialog = new ContentDialog
            {
                Title = row.Name,
                Content = panel,
                PrimaryButtonText = "Apply",
                SecondaryButtonText = "Reset to BIOS default",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            var result = await dialog.ShowAsync();
            switch (result)
            {
                case ContentDialogResult.Primary:
                    if (editor is ComboBox combo) row.SelectedToken = combo.SelectedValue as string ?? row.Item.Value;
                    else if (editor is TextBox box) row.ValueText = box.Text ?? "";
                    break;
                case ContentDialogResult.Secondary:
                    row.ResetToDefault();
                    break;
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }
}
