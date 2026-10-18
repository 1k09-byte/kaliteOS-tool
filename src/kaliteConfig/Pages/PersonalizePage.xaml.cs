using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services.DependencyInjection;
using kaliteConfig.Services.LiveWallpaper;
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
    public LiveWallpaperViewModel LiveViewModel { get; }

    private static readonly string[] LiveVideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv", ".webm", ".mkv" };

    public PersonalizePage()
    {
        InitializeComponent();
        ViewModel = App.Services.Get<PersonalizeViewModel>();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        LiveViewModel = App.Services.Get<LiveWallpaperViewModel>();
        LiveSection.DataContext = LiveViewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSystem();
        _ = LiveViewModel.ReloadLibraryAsync();
        _ = LiveViewModel.ResumeOnLaunchAsync();
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
        LiveSection.Visibility = tag == "live" ? Visibility.Visible : Visibility.Collapsed;
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

    private void LiveLibrary_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LiveWallpaperItem item) LiveViewModel.SelectedItem = item;
    }

    private void LiveLibrary_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void LiveLibrary_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var storage in items)
            {
                try
                {
                    if (storage is Windows.Storage.StorageFile file)
                        await LiveViewModel.AddVideoPathAsync(file.Path);
                    else if (storage is Windows.Storage.StorageFolder folder)
                        await LiveViewModel.AddFolderPathAsync(folder.Path);
                }
                catch { }
            }
        }
        catch { }
    }

    private async void LiveAddVideo_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in LiveVideoExtensions) picker.FileTypeFilter.Add(ext);
        _systemPickerOpen = true;
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await LiveViewModel.AddVideoPathAsync(file.Path);
        }
        catch { }
        finally { _systemPickerOpen = false; }
    }

    private async void LiveAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        _systemPickerOpen = true;
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await LiveViewModel.AddFolderPathAsync(folder.Path);
        }
        catch { }
        finally { _systemPickerOpen = false; }
    }

    private async void LiveAddPackage_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".zip");
        _systemPickerOpen = true;
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await LiveViewModel.AddPackagePathAsync(file.Path);
        }
        catch { }
        finally { _systemPickerOpen = false; }
    }

    private void LiveSetAll_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LiveWallpaperItem item)
        {
            LiveViewModel.SameItem = item;
            LiveViewModel.SelectedItem = item;
        }
    }

    private void LiveRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LiveWallpaperItem item)
        {
            LiveViewModel.SelectedItem = item;
            LiveViewModel.RemoveSelectedCommand.Execute(null);
        }
    }

    private async void LiveRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LiveWallpaperItem item) return;
        LiveViewModel.SelectedItem = item;
        var box = new TextBox { Text = item.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            LiveViewModel.RenameSelected(box.Text);
    }

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
