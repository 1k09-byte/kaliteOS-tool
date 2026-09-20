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
        AttachCaptureService();
        StartQuietRefresh();
        StartFolderWatch();
        if (this.XamlRoot is { } root)
        {
            _lastRasterizationScale = root.RasterizationScale;
            root.Changed += XamlRoot_Changed;
        }
    }

    private void SnipPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopQuietRefresh();
        StopFolderWatch();
        ViewModel.BannerRequested -= OnBannerRequested;
        if (_hostWindow != null) _hostWindow.Activated -= HostWindow_Activated;
        _hostWindow = null;
        if (App.Current.Sniper is { } sniper)
        {
            sniper.HotkeyStatusChanged -= Sniper_HotkeyStatusChanged;
            sniper.CaptureFailed -= Sniper_CaptureFailed;
        }
        if (this.XamlRoot is { } root) root.Changed -= XamlRoot_Changed;
    }

    private static bool _hotkeyNoticeShown;

    private void AttachCaptureService()
    {
        if (App.Current.Sniper is not { } sniper) return;
        sniper.HotkeyStatusChanged -= Sniper_HotkeyStatusChanged;
        sniper.HotkeyStatusChanged += Sniper_HotkeyStatusChanged;
        sniper.CaptureFailed -= Sniper_CaptureFailed;
        sniper.CaptureFailed += Sniper_CaptureFailed;
        ReportHotkeyStatus(once: true);
    }

    private void Sniper_HotkeyStatusChanged() => ReportHotkeyStatus(once: false);

    private void Sniper_CaptureFailed(string reason) =>
        ViewModel.NotifyMessage("Capture failed", reason);

    /// <summary>The hotkey used to fail silently. Now the status bar is truthful and a
    /// registration failure (usually PrtScn owned by Windows Snipping Tool) is reported once.</summary>
    private void ReportHotkeyStatus(bool once)
    {
        if (App.Current.Sniper is not { } sniper) return;
        ViewModel.RefreshHotkeyStatus();
        if (once && _hotkeyNoticeShown) return;
        _hotkeyNoticeShown = true;

        if (!sniper.HotkeyRegistered)
        {
            ViewModel.NotifyMessage("Capture hotkey not registered",
                (string.IsNullOrEmpty(sniper.HotkeyError) ? "No shortcut could be registered." : sniper.HotkeyError)
                + " Use 'Disable Key Hijacking' to reclaim PrtScn (sign out/in once), or pick another hotkey in Snip settings.");
        }
        else if (!string.IsNullOrEmpty(sniper.HotkeyError))
        {
            ViewModel.NotifyMessage("Capture hotkey moved",
                $"Capture now uses {sniper.HotkeyLabel}. {sniper.HotkeyError}");
        }
    }

    // The capture overlay is a separate window: when it closes, the main window
    // reactivates. Refresh then so fresh saves/imports appear in the gallery.
    private void HostWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated;

        // Coming back to the window is when a snip captured in the meantime has to appear. This
        // used to be a full gallery rebuild on every activation, which cleared the selection (and
        // so the details preview) and reloaded every thumbnail -- the refresh the UI was visibly
        // doing on its own. The quiet path only does that work when the folder actually changed.
        if (_windowActive)
        {
            // Opening the burst window here is what makes a capture show up immediately: the
            // overlay closing activates the window, and for the next few seconds the poll is at
            // its tightest interval to pick up the save that came with it.
            NudgeRefreshCadence();
            _ = ViewModel.RefreshQuietAsync();
            // Refocusing with failed cards retries them: cheap, bounded, and the moment the
            // user is most likely staring at a stuck preview.
            RetryFailedPreviews();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _quietRefresh;
    private bool _windowActive = true;

    /// <summary>
    /// The poll's adaptive interval: tight for a few seconds after anything changed, relaxed once
    /// the folder has been quiet. See <see cref="SnipRefreshCadence"/> -- it only trades wakeups
    /// for latency, never what the user sees.
    /// </summary>
    private readonly SnipRefreshCadence _refreshCadence = new();

    /// <summary>
    /// Polls the snips folder so captures and other tools writing into it show up on their own,
    /// without the user ever pressing Refresh.
    ///
    /// The poll is designed to be invisible: it fingerprints the folder first and touches nothing
    /// when the fingerprint is unchanged, and when it did change it merges results into the
    /// existing item instances, so no card re-enters its skeleton state, the scroll position
    /// holds, and the selection (with the details preview) is left alone. Nothing here writes a
    /// status line, opens a banner or shows a busy state, so a poll and a manual refresh look
    /// exactly like an idle page.
    /// </summary>
    private void StartQuietRefresh()
    {
        if (_quietRefresh is not null) return;
        if (DispatcherQueue is null) return;

        _quietRefresh = DispatcherQueue.CreateTimer();
        _quietRefresh.Interval = _refreshCadence.Current;
        _quietRefresh.IsRepeating = true;
        _quietRefresh.Tick += QuietRefresh_Tick;
        _quietRefresh.Start();
    }

    private void StopQuietRefresh()
    {
        var timer = _quietRefresh;
        _quietRefresh = null;
        try { timer?.Stop(); } catch { }
    }

    /// <summary>Re-opens the tight part of the cadence (a snip just landed, or Refresh was pressed).</summary>
    private void NudgeRefreshCadence()
    {
        var interval = _refreshCadence.Nudge();
        if (_quietRefresh is { } timer) timer.Interval = interval;
    }

    private SnipFolderWatcher? _folderWatcher;

    /// <summary>
    /// Watches the snips folder so a save is picked up in the same breath as it happening, rather
    /// than whenever the poll's lazy interval comes around. The callback arrives on a pool thread,
    /// so everything it touches is marshalled back onto the UI thread.
    /// </summary>
    private void StartFolderWatch()
    {
        _folderWatcher ??= SnipFolderWatcher.Start(
            SnipGalleryService.GetSnipsDirectory(), OnSnipsFolderChanged, log: SnipThumbnailItem.ThumbLog);
    }

    private void StopFolderWatch()
    {
        var watcher = _folderWatcher;
        _folderWatcher = null;
        try { watcher?.Dispose(); } catch { }
    }

    private void OnSnipsFolderChanged()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Re-arm either way: if the page is not in a position to poll right now, the interval
            // it resumes with is the tight one.
            NudgeRefreshCadence();
            if (Visibility != Visibility.Visible || XamlRoot is null || !_windowActive) return;
            var changed = false;
            try { changed = await ViewModel.RefreshQuietAsync(); } catch { }
            // A fresh save can land while still being written (non-atomic copy): its first
            // decode may fail and park the card in Failed. A changed merge is exactly when a
            // retry is cheap and likely to succeed, so failed cards reload on their own.
            if (changed) RetryFailedPreviews();
        });
    }

    /// <summary>Re-runs the thumbnail load for cards stuck in Failed/Missing after a merge,
    /// so previews recover without anyone pressing Retry or Refresh.</summary>
    private void RetryFailedPreviews()
    {
        try
        {
            double scale = CurrentRasterizationScale;
            foreach (var item in ViewModel.GallerySnips)
            {
                if (item.Card.IsFailed || item.Card.IsMissing)
                    item.EnsureThumbnailAsync(item.ThumbSize, scale);
            }
            foreach (var recent in ViewModel.RecentSnips)
            {
                if (recent.Card.IsFailed || recent.Card.IsMissing)
                    recent.EnsureThumbnailAsync(240, scale);
            }
        }
        catch { }
    }

    private async void QuietRefresh_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // Never poll while the page is off screen, the window is in the background, or the user is
        // typing into it: a list that re-orders under a caret is exactly the visible refresh this
        // is meant to avoid. A gated tick leaves the cadence alone, so the interval the user comes
        // back to is still the tight one from the last real change.
        if (Visibility != Visibility.Visible || XamlRoot is null || !_windowActive) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;

        var changed = false;
        try { changed = await ViewModel.RefreshQuietAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"quiet refresh failed: {ex.Message}"); }
        if (changed) RetryFailedPreviews();

        // Set after the work, so a merge that takes longer than the interval cannot queue up
        // back-to-back ticks: the next poll is always a full interval after this one finished.
        var next = _refreshCadence.Observe(changed);
        if (_quietRefresh is { } timer) timer.Interval = next;
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
        _ = SnipThumbnailService.CleanupOrphansOnceAsync();
        await ViewModel.RunAutoDeleteAsync();
        await ViewModel.RefreshGalleryAsync();
        ReportHotkeyStatus(once: true);
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
            // The codepoint, written out. Printing the raw glyph here put a private-use
            // character through a text font, which is exactly the tofu box it looked like
            // -- the column is supposed to be readable, and the rendered glyphs already
            // sit next to it at 16/20/24 px.
            var code = new TextBlock
            {
                Text = DescribeGlyph(glyph),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"),
                FontSize = 12,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
            };
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

    /// <summary>"U+E8C8" for a glyph string, so the audit panel is readable text rather than
    /// whatever a private-use codepoint happens to render as in the ambient font.</summary>
    private static string DescribeGlyph(string glyph)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < glyph.Length; i++)
        {
            var codepoint = char.ConvertToUtf32(glyph, i);
            if (char.IsHighSurrogate(glyph[i]) && i + 1 < glyph.Length) i++;
            if (text.Length > 0) text.Append(' ');
            text.Append($"U+{codepoint:X4}");
        }
        return text.Length == 0 ? "-" : text.ToString();
    }

    private async void RefreshGallery_Click(object sender, RoutedEventArgs e)
    {
        // Pressing Refresh explicitly also re-arms the poll, so a capture made one second later
        // is picked up by the tight interval rather than after the lazy one. It also heals
        // stuck previews: a card that failed while its file was still being written would
        // otherwise sit broken forever.
        NudgeRefreshCadence();
        try { await ViewModel.RefreshGalleryAsync(); }
        catch (Exception ex) { ViewModel.NotifyMessage("Refresh failed", ex.Message); }
        RetryFailedPreviews();
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

    private double CurrentRasterizationScale => this.XamlRoot?.RasterizationScale ?? 1.0;

    private void GalleryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            // Recycled: cancel the old card's decode and animations, so no card can ever show
            // another item's thumbnail (this is the documented recycle hook).
            UnhookCard(args.ItemContainer);
        }
        else if (args.ItemContainer.Content is GalleryItem item)
        {
            HookCard(args.ItemContainer, item.Card, "CardImage", "GallerySkeletonHighlight");
            item.EnsureThumbnailAsync(item.ThumbSize, CurrentRasterizationScale);
        }
        else
        {
            // Content not seated yet (virtualization race): re-ask next phase instead of
            // leaving a permanently blank card. Harmless when content is always ready.
            SnipThumbnailItem.ThumbLog("ccc gallery: content not ready, deferring");
            args.RegisterUpdateCallback(GalleryGrid_ContainerDeferred);
        }
        args.Handled = false;
    }

    private void GalleryGrid_ContainerDeferred(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemContainer.Content is GalleryItem item)
        {
            HookCard(args.ItemContainer, item.Card, "CardImage", "GallerySkeletonHighlight");
            item.EnsureThumbnailAsync(item.ThumbSize, CurrentRasterizationScale);
        }
        args.Handled = false;
    }

    private void RecentList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            UnhookCard(args.ItemContainer);
        }
        else if (args.ItemContainer.Content is SnipThumbnailItem item)
        {
            HookCard(args.ItemContainer, item.Card, "CardImage", "RecentSkeletonHighlight");
            item.EnsureThumbnailAsync(240, CurrentRasterizationScale);
        }
        else
        {
            SnipThumbnailItem.ThumbLog("ccc recent: content not ready, deferring");
            args.RegisterUpdateCallback(RecentList_ContainerDeferred);
        }
        args.Handled = false;
    }

    private void RecentList_ContainerDeferred(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemContainer.Content is SnipThumbnailItem item)
        {
            HookCard(args.ItemContainer, item.Card, "CardImage", "RecentSkeletonHighlight");
            item.EnsureThumbnailAsync(240, CurrentRasterizationScale);
        }
        args.Handled = false;
    }

    // ---- card presentation: skeleton pulse + loaded fade ------------------

    private sealed class CardHook
    {
        public SnipCardState? Card;
        public Action? OnLoaded;
        public System.ComponentModel.PropertyChangedEventHandler? OnCardChanged;
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Pulse;
        public UIElement? RevealTarget;
    }

    /// <summary>
    /// Wires one realized container to its card state: the skeleton pulses while loading and the
    /// pixels fade in (150 ms) when they arrive. The hook is stored on the container and removed on
    /// recycle, so it can never fire for the wrong item.
    /// </summary>
    private void HookCard(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, SnipCardState card, string imageName, string skeletonName)
    {
        UnhookCard(container);
        if (container.ContentTemplateRoot is not FrameworkElement root) return;

        var hook = new CardHook { Card = card };

        if (root.FindName(skeletonName) is UIElement skeleton)
            hook.Pulse = StartSkeletonPulse(skeleton);

        var image = root.FindName(imageName) as UIElement;
        hook.RevealTarget = image;

        // A container realized for a card that is already loaded never sees the Loaded event:
        // EnsureAsync short-circuits when the tier is already resident. Apply it right here.
        ApplyCardVisuals(card, image);
        hook.OnLoaded = () => Reveal(image);
        card.Loaded += hook.OnLoaded;

        // Failed / missing states must stop the pulse too (not just the loaded state).
        void OnCardChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SnipCardState.State) && !card.IsLoading && hook.Pulse is not null)
            {
                hook.Pulse.Stop();
                hook.Pulse = null;
            }
            ApplyCardVisuals(card, image);
        }
        hook.OnCardChanged = OnCardChanged;
        card.PropertyChanged += OnCardChanged;

        container.Tag = hook;
    }

    /// <summary>Detaches from the card the container was showing (never the item it now holds).</summary>
    private static void UnhookCard(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container)
    {
        if (container.Tag is not CardHook hook) return;
        container.Tag = null;
        if (hook.Card is not null)
        {
            hook.Card.Cancel();   // stop an in-flight decode for the item this container used to show
            if (hook.OnCardChanged is not null) hook.Card.PropertyChanged -= hook.OnCardChanged;
            if (hook.OnLoaded is not null) hook.Card.Loaded -= hook.OnLoaded;
        }
        hook.Pulse?.Stop();
        hook.Pulse = null;
        // The container is about to be reused for another snip: put the image back to fully
        // opaque so a recycled card can never inherit a hidden image.
        if (hook.RevealTarget is not null) hook.RevealTarget.Opacity = 1;
        hook.RevealTarget = null;
    }

    /// <summary>
    /// Skeleton shimmer while a thumbnail decodes, driven by a UI-thread timer on purpose.
    ///
    /// A Forever storyboard is driven by the render clock, which stops while the window is not
    /// composing (minimised, occluded, or sitting behind the snipping overlay). The animation
    /// then freezes at whatever value it had reached instead of settling, which is what left
    /// cards looking stuck or blank until something forced a repaint. A timer ticks on the UI
    /// thread regardless of what the compositor is doing, and it is stopped as soon as the card
    /// settles, so no clock is left running on a collapsed element.
    /// </summary>
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? StartSkeletonPulse(UIElement skeleton)
    {
        var queue = skeleton.DispatcherQueue;
        if (queue is null) return null;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(60);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            var phase = started.Elapsed.TotalMilliseconds % 2200 / 2200.0;   // one round trip
            var triangle = phase < 0.5 ? phase * 2 : (1 - phase) * 2;        // 0 -> 1 -> 0
            skeleton.Opacity = 0.25 + 0.45 * triangle;
        };
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Pushes a card's current state onto the container it is realised in.
    ///
    /// The template bindings already do this, but they only re-evaluate while the container is
    /// attached and the card raises PropertyChanged for them. A thumbnail that finished decoding
    /// at an awkward moment -- typically while the window was not composing -- left the card
    /// showing its skeleton while the model already held a decoded image, and only a container
    /// re-realise (scroll, view switch, minimise/restore) put pixels on screen. Writing the few
    /// visual properties here means a realised card can never be out of step with its state.
    /// </summary>
    private static void ApplyCardVisuals(SnipCardState card, UIElement? image)
    {
        if (image is not Image target) return;

        target.Source = card.Image;
        target.Stretch = card.ImageStretch;
        target.Width = card.ImageWidth;      // NaN = auto (the 1:1 case)
        target.Height = card.ImageHeight;
        target.Visibility = card.Image is null ? Visibility.Collapsed : Visibility.Visible;
        if (card.Image is not null) target.Opacity = 1;
    }

    /// <summary>
    /// Makes a decoded card image visible.
    ///
    /// This is a plain assignment on purpose, and it used to be a 150 ms opacity storyboard.
    /// Storyboarded opacity is advanced by the render clock, which stops while the window is not
    /// composing -- minimised, occluded, or sitting behind the snipping overlay. A fade that
    /// started in that window froze part-way and held the image at the opacity it had reached,
    /// so the card showed a skeleton forever and only came back once something put the window
    /// back into rendering. That is exactly the "the preview only shows after I take a
    /// screenshot" report: the screenshot returns the window to rendering, the frozen fade
    /// finally finishes, and the pixels appear. Nothing about a card being correct should depend
    /// on an animation clock that may never tick, so the swap is now instantaneous.
    /// </summary>
    private static void Reveal(UIElement? image)
    {
        if (image is null) return;
        image.Opacity = 1;
    }

    // ---- card actions ------------------------------------------------------ 

    private async void CardRetry_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case GalleryItem g:
                await ViewModel.RetryCardAsync(g, g.ThumbSize, CurrentRasterizationScale);
                break;
            case SnipThumbnailItem r:
                await ViewModel.RetryCardAsync(r, 240, CurrentRasterizationScale);
                break;
        }
    }

    private async void CardRemoveMissing_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.DataContext switch
        {
            GalleryItem g => g.Entry.FilePath,
            SnipThumbnailItem r => r.FilePath,
            _ => null,
        };
        if (path is null) return;
        await ViewModel.RemoveFromGalleryAsync(path);
    }

    /// <summary>Moving the window to a monitor with a different scale changes RasterizationScale;
    /// every card re-requests the tier that matches the new physical size.</summary>
    private void XamlRoot_Changed(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        var scale = sender.RasterizationScale;
        if (Math.Abs(scale - _lastRasterizationScale) < 0.001) return;
        _lastRasterizationScale = scale;
        ViewModel.RefreshCardDpi(scale);
    }

    private double _lastRasterizationScale = 1.0;

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

    public void TriggerCaptureFromTray(kaliteConfig.Services.TrayIconService.SnipMode mode)
    {
        var app = (App)Application.Current;

        switch (mode)
        {
            case kaliteConfig.Services.TrayIconService.SnipMode.Region:
                app.Sniper?.TriggerCapture();
                break;
            case kaliteConfig.Services.TrayIconService.SnipMode.Window:
                app.Sniper?.TriggerCapture();
                break;
            case kaliteConfig.Services.TrayIconService.SnipMode.Fullscreen:
                _ = CaptureFullscreenAsync();
                break;
            case kaliteConfig.Services.TrayIconService.SnipMode.Freeform:
                app.Sniper?.TriggerCapture();
                break;
            case kaliteConfig.Services.TrayIconService.SnipMode.Delayed:
                _ = CaptureDelayedAsync();
                break;
        }
    }

    private async System.Threading.Tasks.Task CaptureFullscreenAsync()
    {
        var app = (App)Application.Current;
        if (app.Sniper is null) { ViewModel.NotifyMessage("Capture unavailable", "Capture service is not running."); return; }
        var (ok, msg) = await app.Sniper.CaptureFullscreenAsync();
        ViewModel.NotifyMessage(ok ? "Fullscreen saved" : "Fullscreen failed", msg);
        if (ok) await ViewModel.RefreshGalleryAsync();
    }

    private async System.Threading.Tasks.Task CaptureDelayedAsync()
    {
        var s = SnipSettingsService.Load();
        var delay = Math.Max(1, s.DelayedCaptureSeconds);
        ViewModel.NotifyMessage("Delayed capture", $"Capturing in {delay} seconds...");
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delay));
        var app = (App)Application.Current;
        app.Sniper?.TriggerCapture();
    }

    private async void CaptureTiles_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameworkElement tile) { return; }

        var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(tile);
        var app = (App)Application.Current;

        switch (name)
        {
            case "Capture delayed":
                var s = SnipSettingsService.Load();
                var delay = Math.Max(1, s.DelayedCaptureSeconds);
                ViewModel.NotifyMessage("Delayed capture", $"Capturing in {delay} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delay));
                app.Sniper?.TriggerCapture();
                break;

            case "Capture from clipboard":
                OpenClipboardImageInEditor();
                break;

            case "Repeat last capture":
                if (app.Sniper is null) { ViewModel.NotifyMessage("Repeat unavailable", "Capture service is not running."); break; }
                var (repeatOk, repeatMsg) = await app.Sniper.RepeatLastCaptureAsync();
                ViewModel.NotifyMessage(repeatOk ? "Repeated" : "Nothing to repeat", repeatMsg);
                if (repeatOk) await ViewModel.RefreshGalleryAsync();
                break;

            case "Capture full screen":
                if (app.Sniper is null) { ViewModel.NotifyMessage("Capture unavailable", "Capture service is not running."); break; }
                var (fsOk, fsMsg) = await app.Sniper.CaptureFullscreenAsync();
                ViewModel.NotifyMessage(fsOk ? "Fullscreen saved" : "Fullscreen failed", fsMsg);
                if (fsOk) await ViewModel.RefreshGalleryAsync();
                break;

            default: // Region / Window / Freeform open the region overlay
                app.Sniper?.TriggerCapture();
                break;
        }
    }

    private async void OpenClipboardImageInEditor()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                ViewModel.NotifyMessage("Clipboard empty", "No image found on the clipboard.");
                return;
            }
            var streamRef = await content.GetBitmapAsync();
            using var stream = await streamRef.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var provider = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var pixels = provider.DetachPixelData();
            var app = (App)Application.Current;
            app.Sniper.OpenBitmapInEditor(pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex)
        {
            ViewModel.NotifyMessage("Clipboard capture failed", ex.Message);
        }
    }

    private bool _capturingHotkey;

    private void CaptureHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        CaptureHotkeyButton.Content = "Press keys now… (Esc cancels)";
        CaptureHotkeyButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void CaptureHotkeyButton_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _capturingHotkey = false;
            CaptureHotkeyButton.Content = "Press keys…";
            return;
        }
        uint vk = (uint)e.Key;
        if (!SnipHotkeyService.IsCapturableKey(vk)) return; // bare modifier: keep waiting for the main key
        uint mods = 0;
        var src = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
        if ((src(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= SnipHotkeyService.ModControl;
        if ((src(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= SnipHotkeyService.ModShift;
        if ((src(Windows.System.VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= SnipHotkeyService.ModAlt;
        _capturingHotkey = false;
        CaptureHotkeyButton.Content = "Press keys…";
        ViewModel.ApplyCustomHotkey(mods, vk);
    }

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

    /// <summary>Detaches the selected snip into the full viewer (button or double-tap on the preview).</summary>
    private void DetailsView_Click(object sender, RoutedEventArgs e) => OpenSelectedInViewer();

    private void DetailsPreview_Activated(object sender, EventArgs e) => OpenSelectedInViewer();

    private void OpenSelectedInViewer()
    {
        if (ViewModel.SelectedGalleryItem is not { } item) return;
        try
        {
            var app = (App)Application.Current;
            app.Sniper.OpenInViewer(item.Entry.FilePath);
        }
        catch (Exception ex) { ViewModel.NotifyMessage("Viewer failed", ex.Message); }
    }

    // The details panel used to hook the retry/remove buttons to the shared card handlers, which
    // read DataContext off the button — the panel's DataContext is the view model, so both buttons
    // were dead. They act on the selected item explicitly here.
    private async void DetailsRetry_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGalleryItem is not { } item) return;
        await ViewModel.RetryCardAsync(item, 240, CurrentRasterizationScale);
        DetailsPreview.SourcePath = string.Empty;
        DetailsPreview.SourcePath = item.Entry.FilePath;
    }

    private async void DetailsRemoveMissing_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGalleryItem is not { } item) return;
        await ViewModel.RemoveFromGalleryAsync(item.Entry.FilePath);
    }
}