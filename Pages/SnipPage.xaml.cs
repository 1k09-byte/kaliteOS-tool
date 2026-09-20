using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;
using WinRT.Interop;

namespace kaliteConfig.Pages;

public sealed partial class SnipPage : Page
{
    public SnipViewModel ViewModel => (SnipViewModel)DataContext;
    private Microsoft.UI.Xaml.Window? _hostWindow;

    public SnipPage()
    {
        this.InitializeComponent();
        Loaded += SnipPage_Loaded;
        Unloaded += SnipPage_Unloaded;
    }

    private void SnipPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.BannerRequested += OnBannerRequested;
        _hostWindow = App.MainWindow;
        if (_hostWindow != null) _hostWindow.Activated += HostWindow_Activated;
    }

    private void SnipPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.BannerRequested -= OnBannerRequested;
        if (_hostWindow != null) _hostWindow.Activated -= HostWindow_Activated;
        _hostWindow = null;
    }

    // The capture overlay is a separate window: when it closes, the main window
    // reactivates. Refresh then so fresh saves/imports appear in the gallery.
    private void HostWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            _ = ViewModel.RefreshGalleryAsync();
    }

    private static IntPtr HostHwnd(Page page)
    {
        if (App.MainWindow is Microsoft.UI.Xaml.Window w)
        {
            try
            {
                return WindowNative.GetWindowHandle(w);
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SnipGalleryService.PurgeTrash();
        await ViewModel.RunAutoDeleteAsync();
        await ViewModel.RefreshGalleryAsync();
        GalleryInfoBar.IsOpen = false;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bannerTimer;

    private void OnBannerRequested(string title, string content)
    {
        GalleryInfoBar.Title = title;
        GalleryInfoBar.Message = content;
        GalleryInfoBar.Severity = (title.Contains("fail") || title.Contains("Fail") || title.Contains("unavailable"))
            ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        GalleryInfoBar.IsOpen = true;
        UndoInfoButton.Visibility = ViewModel.HasUndo ? Visibility.Visible : Visibility.Collapsed;

        // Auto-dismiss informational banners after ~5 s (errors stay until closed).
        if (GalleryInfoBar.Severity == InfoBarSeverity.Success)
        {
            _bannerTimer ??= DispatcherQueue.CreateTimer();
            _bannerTimer.Stop();
            _bannerTimer.Interval = TimeSpan.FromSeconds(5);
            _bannerTimer.Tick += (_, _) => { GalleryInfoBar.IsOpen = false; _bannerTimer!.Stop(); };
            _bannerTimer.Start();
        }
    }

    private void HubNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) return;
        var item = args.SelectedItem as NavigationViewItem;
        if (item != null)
        {
            string tag = item.Tag?.ToString() ?? "";
            ViewCapture.Visibility = (tag == "Capture") ? Visibility.Visible : Visibility.Collapsed;
            ViewGallery.Visibility = (tag == "Gallery") ? Visibility.Visible : Visibility.Collapsed;
            ViewTools.Visibility = (tag == "Tools") ? Visibility.Visible : Visibility.Collapsed;
            ViewAutomations.Visibility = (tag == "Automations") ? Visibility.Visible : Visibility.Collapsed;
            ViewSettings.Visibility = (tag == "Settings") ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "Tools" && IconTestPanel.Children.Count == 0) BuildIconTestPanel();
        }
    }

    // Debug-only icon audit: every Snip icon rendered at 16/20/24 px so clipped
    // or wrong glyphs are caught at a glance.
    [System.Diagnostics.Conditional("DEBUG")]
    private void BuildIconTestPanel()
    {
        foreach (var (name, glyph) in SnipIcons.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            var label = new TextBlock { Text = name, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            var code = new TextBlock { Text = glyph, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray), Width = 50, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            row.Children.Add(code);
            foreach (var size in new double[] { 16, 20, 24 })
            {
                row.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Glyph = glyph,
                    FontSize = size,
                    Width = size + 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            IconTestPanel.Children.Add(row);
        }
    }

    private async void RefreshGallery_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshGalleryAsync(); }
        catch (Exception ex) { ViewModel.NotifyMessage("Refresh failed", ex.Message); }
    }

    private async void ImportImages_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var hwnd = HostHwnd(this);
            if (hwnd != IntPtr.Zero) InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            var imported = await SnipGalleryService.ImportImagesAsync(files.Select(f => f.Path).ToList());
            ViewModel.NotifyMessage("Import complete", imported.Count == 1 ? "1 image imported." : $"{imported.Count} images imported.");
            await ViewModel.RefreshGalleryAsync();
        }
        catch (Exception ex)
        {
            ViewModel.NotifyMessage("Import failed", ex.Message);
        }
    }

    private void UndoInfoBar_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UndoDeleteCommand.Execute(null);
        GalleryInfoBar.IsOpen = false;
        _ = ViewModel.RefreshGalleryAsync();
    }

    private async void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CopySelectedAsync();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.DeleteSelectedAsync(); }
        catch (Exception ex) { ViewModel.NotifyMessage("Delete failed", ex.Message); }
    }

    private async void ExportZipSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.HasSelection) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("ZIP archive", new[] { ".zip" });
            picker.SuggestedFileName = $"snips-{DateTime.Now:yyyyMMdd_HHmmss}";
            var hwnd = HostHwnd(this);
            if (hwnd != IntPtr.Zero) InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await ViewModel.ExportSelectedZipAsync(file.Path);
        }
        catch (Exception ex)
        {
            ViewModel.NotifyMessage("Export failed", ex.Message);
        }
    }

    private async void ConvertSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.HasSelection) return;

            var dialog = new ContentDialog
            {
                Title = "Convert selected snips",
                PrimaryButtonText = "Convert",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                Content = new ComboBox
                {
                    Header = "Target format",
                    ItemsSource = new[] { ".png", ".jpg", ".bmp" },
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                },
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            var format = ((ComboBox)dialog.Content).SelectedItem as string ?? ".png";

            var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            folderPicker.FileTypeFilter.Add("*");
            var hwnd = HostHwnd(this);
            if (hwnd != IntPtr.Zero) InitializeWithWindow.Initialize(folderPicker, hwnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null) return;
            await ViewModel.ConvertSelectedAsync(folder.Path, format);
        }
        catch (Exception ex)
        {
            ViewModel.NotifyMessage("Convert failed", ex.Message);
        }
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.HasSelection) return;
            var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            folderPicker.FileTypeFilter.Add("*");
            var hwnd = HostHwnd(this);
            if (hwnd != IntPtr.Zero) InitializeWithWindow.Initialize(folderPicker, hwnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null) return;
            await ViewModel.MoveSelectedAsync(folder.Path);
        }
        catch (Exception ex)
        {
            ViewModel.NotifyMessage("Move failed", ex.Message);
        }
    }

    private void GalleryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var added in e.AddedItems)
        {
            if (added is GalleryItem g) g.IsSelected = true;
        }
        foreach (var removed in e.RemovedItems)
        {
            if (removed is GalleryItem g)
            {
                g.IsSelected = false;
            }
        }
        ViewModel.OnSelectionChanged();
    }

    private void GalleryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.Content is GalleryItem item)
        {
            item.EnsureThumbnailAsync((int)item.ThumbSize);
        }
        args.Handled = false;
    }

    private void RecentList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.Content is SnipThumbnailItem item)
        {
            item.EnsureThumbnailAsync(240);
        }
        args.Handled = false;
    }

    private void GalleryGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0) return;
        var storageItems = new System.Collections.Generic.List<StorageFile>();
        foreach (var obj in e.Items)
        {
            if (obj is GalleryItem g)
            {
                try
                {
                    var f = StorageFile.GetFileFromPathAsync(g.Entry.FilePath).AsTask().Result;
                    if (f != null) storageItems.Add(f);
                }
                catch { }
            }
        }
        if (storageItems.Count > 0)
        {
            e.Data.SetStorageItems(storageItems);
        }
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryItem item)
            item.IsHover = true;
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryItem item)
            item.IsHover = false;
    }

    // ---- Recent Snips hover actions ----

    private SnipThumbnailItem? RecentItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as SnipThumbnailItem;

    private async void RecentCopy_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItem(sender) is not { } item) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            var dp = new DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ViewModel.NotifyMessage("Copied", $"{item.Filename} copied as image.");
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Copy failed", ex.Message); }
    }

    private void RecentEdit_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItem(sender) is { } item)
        {
            try
            {
                var app = (App)Application.Current;
                app.Sniper.OpenInEditor(item.FilePath);
            }
            catch (Exception ex) { ViewModel.NotifyMessage("Edit failed", ex.Message); }
        }
    }

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItem(sender) is not { } item) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{item.FilePath}\""
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Open folder failed", ex.Message); }
    }

    private async void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RecentItem(sender) is not { } item) return;
        try
        {
            await SnipGalleryService.DeleteToTrashAsync(new[] { item.FilePath });
            ViewModel.NotifyMessage("Moved to trash", item.Filename);
            await ViewModel.RefreshGalleryAsync();
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Delete failed", ex.Message); }
    }

    private void ViewAllSnips_Click(object sender, RoutedEventArgs e)
    {
        SelectHubTag("Gallery");
    }

    private void SelectHubTag(string tag)
    {
        foreach (var mi in HubNav.MenuItems.OfType<NavigationViewItem>())
        {
            if (mi.Tag?.ToString() == tag) { HubNav.SelectedItem = mi; return; }
        }
    }

    // ---- Capture tab extras ----

    private void DelayPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            var seconds = DelayPicker.SelectedIndex switch { 1 => 5, 2 => 10, _ => 3 };
            var s = SnipSettingsService.Load();
            s.DelayedCaptureSeconds = seconds;
            SnipSettingsService.Save(s);
        }
        catch { }
    }

    private void ProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { /* applied at capture time */ }

    private void EditProfiles_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NotifyMessage("Capture Profiles",
            "Profiles apply at capture time: Default = copy + save PNG; Bug Report = copy + redact tools; Discord Safe = compressed WebP.");
    }

    private void QuickOpenImage_Click(object sender, RoutedEventArgs e)
    {
        _ = QuickOpenImageAsync();
    }

    private async Task QuickOpenImageAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var hwnd = HostHwnd(this);
            if (hwnd != IntPtr.Zero) InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var app = (App)Application.Current;
            app.Sniper.OpenInEditor(file.Path);
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Open failed", ex.Message); }
    }

    private void QuickCopyText_Click(object sender, RoutedEventArgs e)
        => ViewModel.NotifyMessage("Copy text from screen", "Press the snip hotkey, then use the 'Copy Text (OCR)' toolbar button on the region.");

    private void QuickColorPicker_Click(object sender, RoutedEventArgs e)
        => ViewModel.NotifyMessage("Color picker", "Take a snip first — the color picker lives in the snip overlay toolbar (eyedropper).\n" + SnipIcons.Pick("Eyedropper"));

    private void QuickRuler_Click(object sender, RoutedEventArgs e)
        => ViewModel.NotifyMessage("Ruler", "Take a snip first — the ruler lives in the snip overlay toolbar.\n" + SnipIcons.Pick("Ruler"));

    private void QuickQrScan_Click(object sender, RoutedEventArgs e)
        => ViewModel.NotifyMessage("QR scan", "Take a snip first — QR scan lives in the snip overlay toolbar.\n" + SnipIcons.Pick("QR"));

    // ---- Drop zone ----

    private void SnipDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void SnipDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count == 0) return;
            foreach (var p in paths)
            {
                var app = (App)Application.Current;
                app.Sniper.OpenInEditor(p);
            }
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Drop failed", ex.Message); }
    }

    // ---- Gallery ----

    private void ClearFilters_Click(object sender, RoutedEventArgs e) => ViewModel.ClearFiltersCommand.Execute(null);

    private void StatusBarPrtScn_Tapped(object sender, TappedRoutedEventArgs e) => SelectHubTag("Settings");

    private void DetailsEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGalleryItem is { } item)
        {
            var app = (App)Application.Current;
            app.Sniper.OpenInEditor(item.Entry.FilePath);
        }
    }

    private void DetailsPin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGalleryItem is { } item)
        {
            try
            {
                var app = (App)Application.Current;
                app.Sniper.PinImageFile(item.Entry.FilePath);
            }
            catch (Exception ex) { ViewModel.NotifyMessage("Pin failed", ex.Message); }
        }
    }
}