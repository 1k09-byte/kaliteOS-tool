using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services.DependencyInjection;
using kaliteConfig.Services.Personalize;
using kaliteConfig.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace kaliteConfig.Pages;

public sealed partial class PersonalizePage : Page
{
    private const int KeepSeconds = 15;

    private bool _systemPickerOpen;

    private static readonly string[] WallpaperExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    public PersonalizeViewModel ViewModel { get; }

    public PersonalizePage()
    {
        InitializeComponent();
        ViewModel = App.Services.Get<PersonalizeViewModel>();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSystem();
        if (App.MainWindow is { } window) window.Activated += OnWindowActivated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is { } window) window.Activated -= OnWindowActivated;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (_systemPickerOpen) return;
        RefreshFromSystem();
    }

    private void RefreshFromSystem()
    {
        try
        {
            ViewModel.Reload();
        }
        catch
        {
            return;
        }
        UpdatePreview();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PersonalizeViewModel.SelectedWallpaper):
            case nameof(PersonalizeViewModel.SelectedFit):
                UpdatePreview();
                break;
        }
    }

    private void UpdatePreview()
    {
        var item = ViewModel.SelectedWallpaper;
        if (item is null || string.IsNullOrEmpty(item.FullPath) || !File.Exists(item.FullPath))
        {
            PreviewImage.Source = null;
            return;
        }

        PreviewImage.Source = new BitmapImage(new Uri(item.FullPath)) { DecodePixelWidth = 960 };
        PreviewImage.Stretch = ViewModel.SelectedFit switch
        {
            WallpaperFit.Fit => Stretch.Uniform,
            WallpaperFit.Stretch => Stretch.Fill,
            WallpaperFit.Center or WallpaperFit.Tile => Stretch.None,
            _ => Stretch.UniformToFill,
        };
    }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = (sender.SelectedItem as SelectorBarItem)?.Tag as string;
        WallpaperSection.Visibility = tag == "wallpaper" ? Visibility.Visible : Visibility.Collapsed;
        CursorSection.Visibility = tag == "cursor" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;

        if (SectionBar.SelectedItem is not Microsoft.UI.Xaml.Controls.SelectorBarItem selectedItem ||
            selectedItem.Tag is not string tag || tag != "wallpaper")
        {
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            _systemPickerOpen = true;
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                };
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                foreach (var ext in WallpaperExtensions) picker.FileTypeFilter.Add(ext);

                var file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    await ViewModel.AddWallpaperAsync(file.Path);
                    UpdatePreview();
                }
            }
            catch
            {
            }
            finally
            {
                _systemPickerOpen = false;
            }
        });
    }

    private async void ApplyCursor_Click(object sender, RoutedEventArgs e)
    {
        var results = ViewModel.ApplyCursorOptions();
        if (results.Any(r => r.Ok)) await ConfirmKeepAsync(ViewModel.RevertCursorSection);
    }

    private void PreviewAccent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviewAccentCommand.Execute(null);
    }

    private async void ApplyCursorPack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SelectCursorPackAsync(ViewModel.SelectedCursorPack);
        }
        catch
        {
        }
    }

    private void RevertCursor_Click(object sender, RoutedEventArgs e) => ViewModel.RevertCursorSection();

    private async void PickCursorFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        _systemPickerOpen = true;
        Windows.Storage.StorageFolder? folder;
        try
        {
            folder = await picker.PickSingleFolderAsync();
        }
        catch
        {
            return;
        }
        finally
        {
            _systemPickerOpen = false;
        }

        if (folder is null) return;

        await ViewModel.ImportCursorPackAsync(folder.Path);
    }

    private async void ApplyScheme_Click(object sender, RoutedEventArgs e)
    {
        var results = ViewModel.ApplyCursorScheme();
        if (results.Any(r => r.Ok)) await ConfirmKeepAsync(ViewModel.RevertCursorSection);
    }

    private async Task ConfirmKeepAsync(Action revert)
    {
        var remaining = KeepSeconds;
        var countdown = new TextBlock
        {
            Text = CountdownText(remaining),
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Keep these changes?",
            Content = countdown,
            PrimaryButtonText = "Keep changes",
            CloseButtonText = "Revert",
            DefaultButton = ContentDialogButton.Close,
        };

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                dialog.Hide();
                return;
            }
            countdown.Text = CountdownText(remaining);
        };
        timer.Start();

        var result = await dialog.ShowAsync();
        timer.Stop();

        if (result != ContentDialogResult.Primary) revert();
        ViewModel.Reload();
    }

    private static string CountdownText(int seconds) =>
        $"The cursor change will be reverted in {seconds} seconds unless you keep it.";

    private async void RevertEverything_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Revert everything?",
            Content = "Every setting this app changed will be restored to how it was before the app changed it.",
            PrimaryButtonText = "Revert everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.RevertEverythingCommand.Execute(null);
    }
}
