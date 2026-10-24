using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using kaliteConfig.Models;
using kaliteConfig.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace kaliteConfig.Pages
{
    public sealed partial class BiosManagerPage : Page
    {
        public BiosManagerViewModel ViewModel { get; }

        private bool _syncingSelection;
        private BiosSettingRow? _optionsRow;
        private bool _syncingOptions;

        public BiosManagerPage()
        {
            ViewModel = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<BiosManagerViewModel>(App.Services) ?? new();
            this.InitializeComponent();
            this.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_optionsRow is not null) _optionsRow.PropertyChanged -= OnOptionsRowPropertyChanged;
            _optionsRow = null;
            Unloaded -= OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {

            _syncingSelection = true;
            try
            {
                if (ViewModel.SelectedRow is not null)
                    SettingsGrid.SelectedItem = ViewModel.SelectedRow;
                if (CategoryStrip.SelectedItem != ViewModel.SelectedCategoryItem)
                    CategoryStrip.SelectedItem = ViewModel.SelectedCategoryItem;
                if (ViewModel.SearchText.Length > 0 && SearchField.Text != ViewModel.SearchText)
                    SearchField.Text = ViewModel.SearchText;
            }
            finally { _syncingSelection = false; }
            RebuildValueOptions();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BiosManagerViewModel.SelectedRow))
            {
                if (!_syncingSelection && SettingsGrid.SelectedItem != ViewModel.SelectedRow)
                {
                    _syncingSelection = true;
                    SettingsGrid.SelectedItem = ViewModel.SelectedRow;
                    _syncingSelection = false;
                }
                RebuildValueOptions();
            }
            else if (e.PropertyName == nameof(BiosManagerViewModel.ModifiedCount) &&
                     StagedList.Visibility == Visibility.Visible)
            {
                RefreshStagedList();
            }
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

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportAndLoadCommand.Execute(null);
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SaveFileCommand.Execute(null);
        }

        private void SettingsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {

            _ = DispatcherQueue.TryEnqueue(UpdateTileWidth);
        }

        private void UpdateTileWidth()
        {
            if (SettingsGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

            const double rowHeight = 12 + 16 + 2 + 14;
            const double gutter = 5;
            const double minTile = 240;
            const double maxTile = 380;

            double width = panel.ActualWidth;
            if (width <= gutter) return;

            int columns = (int)Math.Max(1, Math.Ceiling(width / (maxTile + gutter)));
            while (columns > 1 &&
                Math.Floor((width - columns * gutter) / columns) < minTile)
            {
                columns--;
            }

            double tile = Math.Floor((width - columns * gutter) / columns);
            if (tile > maxTile) tile = maxTile;
            if (tile < 180) tile = 180;

            panel.ItemWidth = tile;
            panel.ItemHeight = rowHeight;
        }

        private void ErrorDismiss_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.HasError = false;
        }

        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox box)
                ViewModel.FilterCommand.Execute(box.Text);
        }

        private void SettingsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_syncingSelection) return;
            if (e.ClickedItem is not BiosSettingRow row) return;

            if (InspectorCard.Visibility == Visibility.Collapsed)
            {
                _syncingSelection = true;
                ((GridView)sender).SelectedItem = null;
                _syncingSelection = false;
                _ = ShowQuickEditAsync(row);
                return;
            }

            ViewModel.SelectedRow = row;
            DetailScroll.ChangeView(0, 0, null, true);
        }

        private void RebuildValueOptions()
        {
            if (_optionsRow is not null) _optionsRow.PropertyChanged -= OnOptionsRowPropertyChanged;
            _optionsRow = ViewModel.SelectedRow;
            if (_optionsRow is not null) _optionsRow.PropertyChanged += OnOptionsRowPropertyChanged;
            BuildValueOptions();
        }

        private void OnOptionsRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BiosSettingRow.ValueText) ||
                e.PropertyName == nameof(BiosSettingRow.IsModified))
            {
                SyncValueOptionSelection();
            }
        }

        private void BuildValueOptions()
        {
            ValueOptions.Items.Clear();
            if (_optionsRow is not { IsEnumerated: true } row) return;

            foreach (var option in row.Options)
            {
                ValueOptions.Items.Add(new RadioButton
                {
                    Content = option.FriendlyDisplay,
                    Tag = option.RawToken,
                });
            }

            SyncValueOptionSelection();
        }

        private void ValueOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingOptions) return;
            if (_optionsRow is not { IsEnumerated: true }) return;
            if (ValueOptions.SelectedItem is not RadioButton { Tag: string token }) return;

            _optionsRow.SelectedToken = token;
        }

        private void SyncValueOptionSelection()
        {
            if (_optionsRow is not { IsEnumerated: true } row) return;

            _syncingOptions = true;
            try
            {

                foreach (var item in ValueOptions.Items)
                {
                    if (item is not RadioButton rb) continue;
                    bool isCurrent =
                        (rb.Tag is string token && string.Equals(token, row.Item.Value, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(rb.Content as string, row.Item.Value, StringComparison.OrdinalIgnoreCase);
                    rb.IsChecked = isCurrent;
                }
            }
            finally { _syncingOptions = false; }
        }

        private void TrayToggle_Click(object sender, RoutedEventArgs e)
        {
            bool opening = StagedList.Visibility != Visibility.Visible;
            if (opening) RefreshStagedList();
            StagedList.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            TrayChevron.Glyph = opening ? "\uE70C" : "\uE70E";
        }

        private void RefreshStagedList()
        {
            StagedList.ItemsSource = ViewModel.GetChanges();
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
                if (StagedList.Visibility == Visibility.Visible) RefreshStagedList();
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
