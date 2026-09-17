using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        public BiosManagerViewModel ViewModel { get; } = new();

        public BiosManagerPage()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }

        // Every open of the page auto-exports the live BIOS settings with the
        // bundled SCEWIN tool and shows them (a cached dump is shown instantly
        // while the fresh export runs). Manual "Load dump" still works.
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = ViewModel.AutoLoadAsync();
        }

        // ---- toolbar / overlays ----

        private void LoadDump_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadFileCommand.Execute(null);
        }

        /// <summary>Toolbar: run the bundled SCEWIN tool now and reload the dump.</summary>
        private void ExportLoad_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportAndLoadCommand.Execute(null);
        }

        /// <summary>
        /// Export flow with review: build the Name → Old → New change list and
        /// validation warnings in a dialog; only then open the save picker.
        /// </summary>
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.HasDocument) return;

            var changes = ViewModel.GetChanges();
            if (changes.Count == 0)
            {
                await ShowMessageAsync("Nothing to export", "No settings have been modified. Change a value first, then export.");
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
                    Message = "Revert or fix the highlighted values before exporting.",
                    IsClosable = false,
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = $"{changes.Count} setting(s) will be written:",
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
                PrimaryButtonText = $"Export {changes.Count}…",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = problems.Count == 0,
                XamlRoot = XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.SaveFileCommand.ExecuteAsync(null);
            }
        }

        // ---- list / tree / search events ----

        private void SettingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedRow = e.AddedItems?.OfType<BiosSettingRow>().FirstOrDefault();
        }

        private void SectionTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        {
            var item = args.AddedItems?.FirstOrDefault() as TreeViewItem;
            var section = (item?.Content as FrameworkElement)?.DataContext as BiosMenuSection
                          ?? item?.DataContext as BiosMenuSection;
            ViewModel.SelectSection(section);
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ViewModel.FilterCommand.Execute(sender.Text);
        }

        // ---- quick edit (used when the detail pane is collapsed below 1000px) ----

        private void ValuePill_Tapped(object sender, TappedRoutedEventArgs e)
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
