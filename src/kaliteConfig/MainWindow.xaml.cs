using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using kaliteConfig.Pages;
using kaliteConfig.Services.Navigation;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;

namespace kaliteConfig
{
    public sealed partial class MainWindow : Window
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private kaliteConfig.Services.TrayIconService? _tray;
        private bool _allowExit;
        private bool _trayShown;

        public void ShowMainWindow()
        {
            AppWindow.Show();
            var hwnd = WindowNative.GetWindowHandle(this);
            _ = kaliteConfig.Services.TrayIconService.TrayForeground.BringToFront(hwnd);
        }

        private void SyncNavTopInset()
        {
            try
            {
                double top = titleBar?.ActualHeight ?? 0;
                if (top > 0) NavView.Margin = new Thickness(0, top, 0, 0);
            }
            catch { }
        }

        private void ScheduleTrayRetry()
        {
            const int maxAttempts = 12;

            void Attempt(int attempt)
            {
                if (_trayShown) return;

                EnsureTrayShown();

                if (_trayShown || attempt >= maxAttempts) return;

                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.IsRepeating = false;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Attempt(attempt + 1);
                };
                timer.Start();
            }

            Attempt(0);
        }

        private void EnsureTrayShown()
        {
            if (_trayShown) return;
            try
            {
                var trayIcon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                _trayShown = _tray != null && System.IO.File.Exists(trayIcon) && _tray.Show(trayIcon, "kaliteConfig");
            }
            catch { }
        }

        internal const string ShowWindowEventName = "kaliteConfig_ShowMainWindow";
        internal const string ExitEventName = "kaliteConfig_ExitOldInstance";

        private void StartShowWindowListener()
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var ev = new System.Threading.EventWaitHandle(
                        false, System.Threading.EventResetMode.AutoReset, ShowWindowEventName);
                    while (true)
                    {
                        ev.WaitOne();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            ShowMainWindow();
                            try
                            {
                                if (App.Services.GetService(typeof(Services.Notifications.IAppNotificationService)) is Services.Notifications.IAppNotificationService svc)
                                    svc.Post("Already running", "Opened the existing kaliteConfig window. If you just updated, right-click the tray icon, choose Exit, then launch again.", Services.Notifications.AppNoticeSeverity.Info);
                            }
                            catch { }
                        });
                    }
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void StartExitListener()
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var ev = new System.Threading.EventWaitHandle(
                        false, System.Threading.EventResetMode.AutoReset, ExitEventName);
                    ev.WaitOne();
                    DispatcherQueue.TryEnqueue(AllowExitAndClose);
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        public void ShutdownTray()
        {
            try { _tray?.Dispose(); } catch { }
            _tray = null;
        }

        public void AllowExitAndClose()
        {
            App.ShutdownTrace("settings-exit.begin");
            PrepareForUpdateShutdown();
            App.ShutdownTrace("settings-exit.prepared");
            this.Close();
            App.ShutdownTrace("settings-exit.closed-returned");
        }

        public void PrepareForUpdateShutdown()
        {
            App.ShutdownTrace("prepare.begin");
            _allowExit = true;
            try { (Application.Current as App)?.LegacyGamingMode.ReleaseAll(); } catch { }
            App.ShutdownTrace("prepare.gaming-released");
            try { (Application.Current as App)?.ForegroundSuspend.ResumeAllSync(); } catch { }
            App.ShutdownTrace("prepare.foreground-resumed");
            try { _tray?.Dispose(); } catch { }
            _tray = null;
            App.ShutdownTrace("prepare.end");
        }

        public MainWindow()
        {
            InitializeComponent();

            NavRailMask.Background = _railMaskBrush;
            _titleFadeBrush.StartPoint = new Windows.Foundation.Point(0, 0);
            _titleFadeBrush.EndPoint = new Windows.Foundation.Point(0, 1);
            _titleFadeBrush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0 });
            _titleFadeBrush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 });
            TitleFade.Background = _titleFadeBrush;
            _backdropTintBrush = new SolidColorBrush();
            TintLayer.Background = _backdropTintBrush;
            EnsureDefaultNavyTint();
            ApplyBackgroundImage();
            ApplyBackdropTint(kaliteConfig.Services.BackdropTint.Load());
            try
            {
                titleBar.SizeChanged += (_, _) => SyncNavTopInset();
                SyncNavTopInset();
            }
            catch { }

            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                int preference = DWMWCP_ROUND;
                kaliteConfig.Native.DwmApi.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
            }

            ExtendsContentIntoTitleBar = true;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                try { presenter.PreferredMinimumWidth = 1100; }
                catch {  }
            }
            AppWindow.Title = "kaliteConfig";
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { }

            _tray = new kaliteConfig.Services.TrayIconService();
            _tray.OnOpen += () =>
            {
                DispatcherQueue.TryEnqueue(ShowMainWindow);
            };
            _tray.LiveStateQuery = () =>
            {
                try
                {
                    var live = App.Services?.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                    if (live is null) return (false, false);
                    bool active = live.State == kaliteConfig.Services.LiveWallpaper.LiveState.Playing || live.State == kaliteConfig.Services.LiveWallpaper.LiveState.Paused;
                    return (active, active);
                }
                catch { return (false, false); }
            };
            _tray.OnPauseLive += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var live = App.Services?.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                        if (live?.State == kaliteConfig.Services.LiveWallpaper.LiveState.Playing) live.Pause();
                        else if (live?.State == kaliteConfig.Services.LiveWallpaper.LiveState.Paused) live.Resume();
                    }
                    catch { }
                });
            };
            _tray.GameModeHeldQuery = () =>
            {
                try { return (Application.Current as App)?.GameMode.PendingJournalCount > 0; } catch { return false; }
            };
            _tray.OnResumeGame += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { (Application.Current as App)?.GameMode.ResumeEverything(); } catch { }
                });
            };
            _tray.OnStopLive += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var live = App.Services?.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                        live?.StopAndRestore();
                    }
                    catch { }
                });
            };
            _tray.OnExit += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    App.ShutdownTrace("tray-exit.dequeued");
                    _allowExit = true;
                    try { (Application.Current as App)?.LegacyGamingMode.ReleaseAll(); } catch { }
                    App.ShutdownTrace("tray-exit.gaming-released");
                    try { (Application.Current as App)?.ForegroundSuspend.ResumeAllSync(); } catch { }
                    App.ShutdownTrace("tray-exit.foreground-resumed");
                    try { _tray?.Dispose(); } catch { }
                    _tray = null;
                    App.ShutdownTrace("tray-exit.tray-disposed");
                    this.Close();
                    App.ShutdownTrace("tray-exit.close-returned");
                });
            };
            int liveResumeAttempts = 0;
            var liveResumeTimer = DispatcherQueue.CreateTimer();
            liveResumeTimer.Interval = TimeSpan.FromSeconds(2);
            liveResumeTimer.IsRepeating = false;
            liveResumeTimer.Tick += async (_, _) =>
            {
                liveResumeTimer.Stop();
                try
                {
                    var live = App.Services?.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                    if (live is null) return;
                    var (ok, _) = await live.TryResumeOnLaunchAsync();
                    if (ok || ++liveResumeAttempts >= 4) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        liveResumeTimer.Interval = TimeSpan.FromSeconds(3);
                        liveResumeTimer.Start();
                    });
                }
                catch { }
            };
            liveResumeTimer.Start();
            _tray.OnTakeScreenshot += (mode) =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var app = (App)Application.Current;
                    if (app.Sniper is null) return;

                    switch (mode)
                    {
                        case kaliteConfig.Services.TrayIconService.SnipMode.Region:
                            app.Sniper.TriggerCapture();
                            break;
                        case kaliteConfig.Services.TrayIconService.SnipMode.Window:
                            app.Sniper.TriggerCapture();
                            break;
                        case kaliteConfig.Services.TrayIconService.SnipMode.Fullscreen:
                            var (fsOk, fsMsg) = await app.Sniper.CaptureFullscreenAsync();
                            break;
                        case kaliteConfig.Services.TrayIconService.SnipMode.Freeform:
                            app.Sniper.TriggerCapture();
                            break;
                        case kaliteConfig.Services.TrayIconService.SnipMode.Delayed:
                            var s = kaliteConfig.Services.SnipSettingsService.Load();
                            var delay = Math.Max(1, s.DelayedCaptureSeconds);
                            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delay));
                            app.Sniper.TriggerCapture();
                            break;
                    }
                });
            };
            try
            {
                var trayIcon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                _trayShown = System.IO.File.Exists(trayIcon) && _tray.Show(trayIcon, "kaliteConfig");
            }
            catch { }

            if (!_trayShown)
                ScheduleTrayRetry();
            StartShowWindowListener();
            StartExitListener();
            try
            {
                GpuOverclock.GpuOverclockModule.Instance.GameAutoApply.StatusChanged += status =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            var tip = "kaliteConfig";
                            if (!string.IsNullOrWhiteSpace(status.ActiveProfileName))
                                tip += $" · {status.ActiveProfileName}";
                            _tray?.UpdateTooltip(tip);
                        }
                        catch { }
                    });
                };
            }
            catch { }
        AppWindow.Closing += (_, args) =>
        {
            if (_allowExit) return;
            EnsureTrayShown();
            if (!_trayShown) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
            AppWindow.Changed += (_, args) =>
            {
                if (!args.DidPresenterChange) return;
                try
                {
                    if (AppWindow.Presenter is OverlappedPresenter overlapped
                        && overlapped.State == OverlappedPresenterState.Minimized)
                    {
                        EnsureTrayShown();
                        if (_trayShown) AppWindow.Hide();
                    }
                }
                catch { }
            };
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            WatchMaterialChanges();
            HookNotifications();
            this.Activated += (_, _) => GuardedActivation(() => WatchMaterialChanges());
            this.Activated += (_, _) => { try { Services.Discord.DiscordPresence.ReassertActivity(); } catch { } };

            NavShell.LayoutUpdated += (_, _) => UpdateNavPill();

            bool startHidden = Services.NavLayoutPreference.Layout == Services.NavLayoutPreference.HiddenTag;
            string startTag = (Services.NavLayoutPreference.StartOnHome && startHidden) ? "HomePage" : "AppsPage";
            var startItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == startTag)
                ?? NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
            _suppressRailPop = true;
            if (startItem != null)
                NavView.SelectedItem = startItem;
            _suppressRailPop = false;

            NavView.Loaded += (_, _) =>
            {
                SuppressSidebarTooltips();
            };
            SuppressSidebarTooltips();
            ApplyNavLayout();
        }

        private DispatcherTimer? _noticeTimer;

        private void HookNotifications()
        {
            try
            {
                if (App.Services.GetService(typeof(Services.Notifications.IAppNotificationService)) is not Services.Notifications.IAppNotificationService service) return;
                service.Posted += notice => DispatcherQueue.TryEnqueue(() => ShowNotice(notice));
            }
            catch { }
        }

        private void ShowNotice(Services.Notifications.AppNotice notice)
        {
            try
            {
                AppNoticeBar.Title = notice.Title;
                AppNoticeBar.Message = notice.Message;
                AppNoticeBar.Severity = notice.Severity switch
                {
                    Services.Notifications.AppNoticeSeverity.Success => InfoBarSeverity.Success,
                    Services.Notifications.AppNoticeSeverity.Warning => InfoBarSeverity.Warning,
                    Services.Notifications.AppNoticeSeverity.Error => InfoBarSeverity.Error,
                    _ => InfoBarSeverity.Informational,
                };
                AppNoticeBar.IsOpen = true;
                _noticeTimer ??= CreateNoticeTimer();
                _noticeTimer.Stop();
                _noticeTimer.Start();
            }
            catch { }
        }

        private DispatcherTimer CreateNoticeTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                AppNoticeBar.IsOpen = false;
            };
            return timer;
        }

        private bool _materialWatchAttached;

        private void GuardedActivation(Action work)
        {
            try
            {
                work();
            }
            catch {  }
        }

        private void WatchMaterialChanges()
        {
            OnThemeChanged();
            if (_materialWatchAttached) return;
            var svc = (Application.Current as App)?.ThemeService;
            if (svc == null) return;
            try
            {
                svc.BackdropChanged += (_, _) => DispatcherQueue.TryEnqueue(OnThemeChanged);
                svc.ThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(OnThemeChanged);
                _materialWatchAttached = true;
                OnThemeChanged();
            }
            catch { }

        }

        private static void EnsureDefaultNavyTint()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "kaliteConfig");
                string marker = System.IO.Path.Combine(dir, "navy-tint-auto.txt");
                if (System.IO.File.Exists(marker)) return;
                if (!kaliteConfig.Services.BackdropTint.HasSavedFile())
                    kaliteConfig.Services.BackdropTint.Save(0x19, 0x23, 0x5A,
                        kaliteConfig.Services.BackdropTint.DefaultStrengthPercent);
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                }
                catch { }
            }
            catch { }
        }

        public void ApplyBackdropTint((byte R, byte G, byte B, byte A) tint)
        {
            if (_backdropTintBrush is null) return;
            bool isLight = RootGrid.ActualTheme == ElementTheme.Light;
            if (isLight)
                tint = (tint.R, tint.G, tint.B, (byte)(tint.A * 0.4));
            _backdropTintBrush.Color = Windows.UI.Color.FromArgb(tint.A, tint.R, tint.G, tint.B);
            kaliteConfig.Services.SurfaceTint.Apply(tint, isLight);

            NavRailMask.Visibility = Visibility.Collapsed;
            var paneColor = Microsoft.UI.Colors.Transparent;
            if (_titleFadeBrush.GradientStops.Count >= 2)
            {
                _titleFadeBrush.GradientStops[0].Color = Microsoft.UI.Colors.Transparent;
                _titleFadeBrush.GradientStops[1].Color = Microsoft.UI.Colors.Transparent;
            }
            var rail = NavView.Resources;
            if (rail is null) return;
            if (rail["NavigationViewBackground"] is SolidColorBrush navBackground)
                navBackground.Color = paneColor;
            if (rail["NavigationViewDefaultPaneBackground"] is SolidColorBrush paneBackground)
                paneBackground.Color = paneColor;
            if (rail["NavigationViewExpandedPaneBackground"] is SolidColorBrush expandedPaneBackground)
                expandedPaneBackground.Color = paneColor;
            byte ink = isLight ? (byte)0x00 : (byte)0xFF;
            byte wr = tint.A > 0 ? tint.R : ink;
            byte wg = tint.A > 0 ? tint.G : ink;
            byte wb = tint.A > 0 ? tint.B : ink;
            SetRailWash("RailHighlightSelectedBrush", 0x4D, wr, wg, wb);
            SetRailWash("RailHighlightHoverBrush", 0x38, wr, wg, wb);
            SetRailWash("RailHighlightPressedBrush", 0x2E, wr, wg, wb);
        }
        private void SetRailWash(string key, byte a, byte r, byte g, byte b)
        {
            try
            {
                var brush = kaliteConfig.Services.Theming.ThemeHelper.ThemedBrush(
                    key, RootGrid.ActualTheme == ElementTheme.Light);
                if (brush is not null)
                    brush.Color = Windows.UI.Color.FromArgb(a, r, g, b);
            }
            catch { }
        }
        private Windows.UI.Color CurrentCardColor()
        {
            var brush = kaliteConfig.Services.Theming.ThemeHelper.ThemedBrush(
                "CardBackgroundFillColorDefaultBrush",
                RootGrid.ActualTheme == ElementTheme.Light);
            return brush?.Color ?? Windows.UI.Color.FromArgb(0xB3, 0x12, 0x12, 0x12);
        }

        private SolidColorBrush _backdropTintBrush = new();
        private readonly SolidColorBrush _railMaskBrush = new();
        private readonly LinearGradientBrush _titleFadeBrush = new();

        private int? MaybeWindowPixelWidth()
        {
            try
            {
                if (AppWindow?.Size.Width > 0)
                    return (int)AppWindow.Size.Width;
            }
            catch { }
            return null;
        }

        private double? MaybeWindowAspect()
        {
            try
            {
                if (AppWindow?.Size.Width > 0 && AppWindow?.Size.Height > 0)
                    return (double)AppWindow.Size.Width / AppWindow.Size.Height;
            }
            catch { }
            return null;
        }

        private double? MaybeImageAspect(Microsoft.UI.Xaml.Media.Imaging.BitmapImage bmp)
        {
            try
            {
                if (bmp?.PixelWidth > 0 && bmp?.PixelHeight > 0)
                    return (double)bmp.PixelWidth / bmp.PixelHeight;
            }
            catch { }
            return null;
        }

        public void ApplyBackgroundImage()
        {
            LoadBackgroundImage();
            ApplyBackdropTint(kaliteConfig.Services.BackdropTint.Load());
        }

        private void LoadBackgroundImage()
        {
            var pref = kaliteConfig.Services.BackgroundImageService.Load();
            if (string.IsNullOrWhiteSpace(pref.Path))
            {
                BgImageLayer.Visibility = Visibility.Collapsed;
                BgImage.Visibility = Visibility.Collapsed;
                return;
            }

            if (!System.IO.File.Exists(pref.Path))
            {
                BgImageLayer.Visibility = Visibility.Collapsed;
                BgImage.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var winW = (int?)MaybeWindowPixelWidth();
                var decodeW = winW.HasValue ? Math.Max(256, Math.Min(winW.Value, 2560)) : Math.Max(256, 1920);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelWidth = decodeW;
                bmp.UriSource = new Uri(pref.Path);

                var stretch = pref.Stretch;
                if (stretch == "Auto")
                {
                    var imgAspect = (double?)MaybeImageAspect(bmp) ?? (double)decodeW / Math.Max(1, decodeW);
                    var winAspect = (double?)MaybeWindowAspect() ?? (double)Math.Max(1, decodeW) / Math.Max(1, decodeW);
                    stretch = (imgAspect >= winAspect) ? "UniformToFill" : "Uniform";
                }

                BgImage.Source = bmp;
                BgImage.Stretch = stretch switch
                {
                    "None" => Microsoft.UI.Xaml.Media.Stretch.None,
                    "Fill" => Microsoft.UI.Xaml.Media.Stretch.Fill,
                    "UniformToFill" => Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                    _ => Microsoft.UI.Xaml.Media.Stretch.Uniform,
                };
                BgImage.Opacity = pref.Opacity / 100.0;
                BgImage.Visibility = Visibility.Visible;
                BgImageLayer.Visibility = Visibility.Visible;
            }
            catch
            {
                BgImageLayer.Visibility = Visibility.Collapsed;
                BgImage.Visibility = Visibility.Collapsed;
            }
        }

        public void ApplyBackgroundImageOpacity(byte opacityPercent)
        {
            BgImage.Opacity = Math.Clamp((int)opacityPercent, 0, 100) / 100.0;
        }

        private void OnThemeChanged()
        {
            try
            {
                if (SystemBackdrop is null)
                    RootGrid.Background = new SolidColorBrush(RootGrid.ActualTheme == ElementTheme.Light
                        ? Microsoft.UI.Colors.White
                        : Microsoft.UI.Colors.Black);
                else RootGrid.Background = null;
            }
            catch { }
            kaliteConfig.Services.SurfaceTint.ResetCapture(RootGrid.ActualTheme == ElementTheme.Light);
            ApplyBackdropTint(kaliteConfig.Services.BackdropTint.Load());
        }

        private void SuppressSidebarTooltips()
        {
            foreach (var item in System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.MenuItems)
                .Concat(System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.FooterMenuItems)))
            {
                ToolTipService.SetToolTip(item, new ToolTip { Visibility = Visibility.Collapsed });
            }
            HideNavScrollbars(NavView);
        }

        private NavigationViewItem? _railBodyOwner;
        private Border? _railBodyBox;

        private Border? RailItemBody(NavigationViewItem item)
        {
            if (!ReferenceEquals(_railBodyOwner, item))
            {
                _railBodyOwner = item;
                _railBodyBox = FindDescendant<Border>(item, "ItemBody");
            }
            return _railBodyBox;
        }

        private static void HideNavScrollbars(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv)
                {
                    sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                    sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    sv.VerticalScrollMode = ScrollMode.Auto;
                    sv.HorizontalScrollMode = ScrollMode.Disabled;
                }
                HideNavScrollbars(child);
            }
        }

        private bool _pillEnabled = true;

        private void UpdateNavPill(NavigationViewItem? item = null)
        {
            if (!_pillEnabled)
            {
                NavPill.Opacity = 0;
                return;
            }
            if (!NavView.IsPaneVisible)
            {
                NavPill.Opacity = 0;
                return;
            }
            item ??= NavView.SelectedItem as NavigationViewItem;
            if (item is null || item.ActualHeight <= 0 || NavShell.ActualHeight <= 0)
            {
                NavPill.Opacity = 0;
                return;
            }

            var pos = item.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double top = pos.Y + ((item.ActualHeight - NavPill.Height) / 2);

            const double pillInset = 0;

            var box = RailItemBody(item);
            if (box is null || box.ActualWidth <= 0)
            {
                NavPill.Opacity = 0;
                return;
            }

            if (_navPillGlide is not null)
            {
                return;
            }

            var boxPos = box.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double left = boxPos.X + pillInset;

            double renderedLeft = NavPill.Margin.Left + NavPillShift.X;
            double renderedTop = NavPill.Margin.Top + NavPillShift.Y;

            bool needsMove = Math.Abs(renderedTop - top) > 0.5 || Math.Abs(renderedLeft - left) > 0.5;
            bool firstPlacement = !_navPillPlaced;

            if (!needsMove && !firstPlacement)
            {
                return;
            }

            double offsetY = renderedTop - top;
            double offsetX = renderedLeft - left;

            NavPill.Margin = new Thickness(left, top, 0, 0);
            AnimateNavPillToRest(offsetX, offsetY, firstPlacement);
        }

        private Storyboard? _navPillGlide;
        private bool _navPillPlaced;

        private void AnimateNavPillToRest(double offsetX, double offsetY, bool fadeInOnly)
        {
            _navPillGlide?.Stop();
            _navPillGlide = null;

            if (!fadeInOnly && Math.Abs(offsetX) <= 0.5 && Math.Abs(offsetY) <= 0.5)
            {
                NavPillShift.X = 0;
                NavPillShift.Y = 0;
                _navPillPlaced = true;
                NavPill.Opacity = 1;
                return;
            }

            var sb = new Storyboard();

            if (fadeInOnly)
            {
                sb.Children.Add(PillAnimation(NavPill, "Opacity", 0, 1, NavPillFadeDuration));
            }
            else
            {
                sb.Children.Add(PillAnimation(NavPillShift, "Y", offsetY, 0, NavPillGlideDuration));
                sb.Children.Add(PillAnimation(NavPillShift, "X", offsetX, 0, NavPillGlideDuration));
                sb.Children.Add(PillAnimation(NavPill, "Opacity", 0.55, 1, NavPillGlideDuration));
            }

            sb.Completed += (_, _) =>
            {
                _navPillGlide = null;
                NavPillShift.X = 0;
                NavPillShift.Y = 0;
                UpdateNavPill();
            };

            _navPillGlide = sb;
            _navPillPlaced = true;
            sb.Begin();
        }

        private static readonly TimeSpan NavPillGlideDuration = TimeSpan.FromMilliseconds(190);
        private static readonly TimeSpan NavPillFadeDuration = TimeSpan.FromMilliseconds(160);

        private static DoubleAnimation PillAnimation(DependencyObject target, string property, double from, double to, TimeSpan duration)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(duration),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            return anim;
        }

        private bool _navigating;

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_navigating) return;
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
                GoToPage(NavigationService.KeyForTag(tag), tag);
        }

        public void GoToPage(PageKey key, string? tag)
        {
            if (_navigating) return;
            _navigating = true;
            try
            {
                var nav = App.Services.GetService(typeof(INavigationService)) as INavigationService;
                nav?.Attach(ContentFrame);
                nav?.Navigate(key);
                try { Services.Discord.DiscordPresence.UpdateActivity(key); } catch { }
                PlayPageEntrance();
                _suppressRailPop = true;
                NavigationViewItem? railItem = null;
                try { railItem = SyncRailSelection(tag); }
                finally { _suppressRailPop = false; }
                SyncDockSelection(key);
                if (NavView.IsPaneVisible && railItem is not null)
                {
                    UpdateNavPill(railItem);
                    AnimateRailItemPop(railItem);
                }
                else
                {
                    UpdateNavPill();
                }
            }
            catch { }
            finally { _navigating = false; }
        }

        private NavigationViewItem? SyncRailSelection(string? tag)
        {
            if (tag is null) return NavView.SelectedItem as NavigationViewItem;
            var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag)
                ?? NavView.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag);
            if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
                NavView.SelectedItem = item;
            return item ?? NavView.SelectedItem as NavigationViewItem;
        }

        private void TopHomeButton_Click(object sender, RoutedEventArgs e) => GoToPage(PageKey.Home, "HomePage");

        public void ApplyNavLayout()
        {
            string layout = Services.NavLayoutPreference.Layout;
            bool rail = layout == Services.NavLayoutPreference.RailTag;
            bool top = layout == Services.NavLayoutPreference.TopTag;
            bool dock = layout == Services.NavLayoutPreference.DockTag;
            bool hidden = layout == Services.NavLayoutPreference.HiddenTag;
            try
            {
                NavView.PaneDisplayMode = top ? NavigationViewPaneDisplayMode.Top : NavigationViewPaneDisplayMode.Left;
                NavView.IsPaneVisible = rail || top;
                NavView.OpenPaneLength = 76;
                NavView.IsPaneOpen = true;
                _pillEnabled = rail || top;
                if (!_pillEnabled)
                {
                    try { _navPillGlide?.Stop(); } catch { }
                    _navPillGlide = null;
                    _navPillPlaced = false;
                    NavPill.Opacity = 0;
                }
                NavRailMask.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
                HomeRailItem.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
                TopHomeButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
                titleBar.Margin = hidden ? new Thickness(96, 0, 0, 0) : new Thickness(0, 0, 0, 0);
                ContentFrame.Margin = dock ? new Thickness(0, 0, 0, 72) : new Thickness(0, 0, 0, 0);
            }
            catch { }
            try
            {
                if (!dock)
                {
                    StopDockPeekTimer();
                    try { _dockSlide?.Stop(); } catch { }
                    _dockSlide = null;
                    BottomDock.Opacity = 1;
                    DockShift.Y = 0;
                    _dockEntrancePlayed = false;
                    foreach (var button in DockButtons.Children.OfType<Button>())
                    {
                        button.Opacity = 1;
                        if (button.RenderTransform is ScaleTransform resetScale)
                        {
                            resetScale.ScaleX = 1;
                            resetScale.ScaleY = 1;
                        }
                    }
                }
                BottomDock.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
                if (dock) EnsureDockBuilt();
                SyncDockHomeVisibility(hidden);
                if (dock) DockPlayEntrance();
            }
            catch
            {
                try { BottomDock.Opacity = 1; } catch { }
            }
            try
            {
                var current = (App.Services.GetService(typeof(INavigationService)) as INavigationService)?.Current;
                if (!hidden && current == PageKey.Home)
                {
                    GoToPage(PageKey.Apps, "AppsPage");
                    current = PageKey.Apps;
                }
                SyncDockSelection(current);
                UpdateNavPill();
            }
            catch { }
            try { DispatcherQueue.TryEnqueue(() => HideNavScrollbars(NavView)); } catch { }
        }

        private bool _dockBuilt;

        private void EnsureDockBuilt()
        {
            if (_dockBuilt) return;
            _dockBuilt = true;
            foreach (var entry in DockEntries)
            {
                IconElement icon;
                try { icon = Services.NavIcons.Create(entry.Glyph, entry.Path, entry.SymbolFont); }
                catch { continue; }
                if (icon is PathIcon pathIcon) { pathIcon.Width = 20; pathIcon.Height = 20; }
                else if (icon is FontIcon fontIcon) { fontIcon.FontSize = 20; }
                icon.VerticalAlignment = VerticalAlignment.Center;
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var label = new TextBlock
                {
                    Text = entry.Label,
                    FontSize = 10,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 64,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var cell = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(icon);
                cell.Children.Add(label);
                var button = new Button
                {
                    Content = cell,
                    Tag = entry.Key,
                    Padding = new Thickness(10, 8, 10, 6),
                    MinWidth = 56,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0, 0, 0, 0),
                    UseSystemFocusVisuals = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, entry.Label);
                ToolTipService.SetToolTip(button, entry.Label);
                PageKey key = entry.Key;
                string tag = entry.Tag;
                button.Click += (_, _) => GoToPage(key, tag);
                DockButtons.Children.Add(button);
            }
            SyncDockSelection((App.Services.GetService(typeof(INavigationService)) as INavigationService)?.Current, true);
        }

        private Storyboard? _dockSlide;

        private bool _dockEntrancePlayed;

        private void BottomDock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (BottomDock.Visibility != Visibility.Visible) return;
            if (!_dockEntrancePlayed && BottomDock.ActualHeight > 0)
            {
                DockPlayEntrance();
                return;
            }
            if (BottomDock.Opacity < 1) BottomDock.Opacity = 1;
            if (DockShift.Y <= 0.5) DockShift.Y = 0;
            else DockApplyPeek(false);
        }

        private void BottomDock_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            StopDockPeekTimer();
            if (DockShift.Y <= 0.5) return;
            DockSlideTo(0);
        }

        private void BottomDock_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            double peek = BottomDock.ActualHeight > 0 ? BottomDock.ActualHeight * 0.75 : 48;
            if (DockShift.Y >= peek - 0.5) return;
            StartDockPeekTimer();
        }

        private DispatcherTimer? _dockPeekTimer;

        private void StartDockPeekTimer()
        {
            try
            {
                _dockPeekTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _dockPeekTimer.Tick -= DockPeekTimer_Tick;
                _dockPeekTimer.Tick += DockPeekTimer_Tick;
                _dockPeekTimer.Stop();
                _dockPeekTimer.Start();
            }
            catch { DockApplyPeek(true); }
        }

        private void StopDockPeekTimer()
        {
            try { _dockPeekTimer?.Stop(); } catch { }
        }

        private void DockPeekTimer_Tick(object? sender, object e)
        {
            StopDockPeekTimer();
            if (BottomDock.Visibility == Visibility.Visible) DockApplyPeek(true);
        }

        private void SyncDockHomeVisibility(bool hidden)
        {
            if (!_dockBuilt) return;
            foreach (var button in DockButtons.Children.OfType<Button>())
            {
                if (button.Tag is PageKey k && k == PageKey.Home)
                    button.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void DockLeft_Click(object sender, RoutedEventArgs e)
        {
            try { DockScroller.ChangeView(Math.Max(0, DockScroller.HorizontalOffset - 240), null, null, false); } catch { }
        }

        private void DockRight_Click(object sender, RoutedEventArgs e)
        {
            try { DockScroller.ChangeView(DockScroller.HorizontalOffset + 240, null, null, false); } catch { }
        }

        private void DockApplyPeek(bool animate)
        {
            double peek = BottomDock.ActualHeight > 0 ? BottomDock.ActualHeight * 0.75 : 48;
            DockSlideTo(peek, animate);
        }

        private void DockPlayEntrance()
        {
            try
            {
                if (BottomDock.ActualHeight <= 0)
                {
                    BottomDock.Opacity = 1;
                    DockShift.Y = 48;
                    return;
                }
                double peek = BottomDock.ActualHeight * 0.75;
                DockShift.Y = peek + 24;
                BottomDock.Opacity = 0;
                var fade = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                };
                Storyboard.SetTarget(fade, BottomDock);
                Storyboard.SetTargetProperty(fade, "Opacity");
                var rise = new DoubleAnimation
                {
                    To = peek,
                    Duration = new Duration(TimeSpan.FromMilliseconds(340)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(rise, DockShift);
                Storyboard.SetTargetProperty(rise, "Y");
                var sb = new Storyboard();
                sb.Children.Add(fade);
                sb.Children.Add(rise);
                int i = 0;
                foreach (var button in DockButtons.Children.OfType<Button>())
                {
                    button.Opacity = 0;
                    var pop = new DoubleAnimation
                    {
                        To = 1,
                        Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                        BeginTime = TimeSpan.FromMilliseconds(60 + i * 18),
                    };
                    Storyboard.SetTarget(pop, button);
                    Storyboard.SetTargetProperty(pop, "Opacity");
                    sb.Children.Add(pop);
                    i++;
                }
                _dockSlide?.Stop();
                _dockSlide = sb;
                sb.Completed += (_, _) =>
                {
                    _dockSlide = null;
                    _dockEntrancePlayed = true;
                    BottomDock.Opacity = 1;
                };
                sb.Begin();
            }
            catch
            {
                BottomDock.Opacity = 1;
                _dockEntrancePlayed = true;
                DockApplyPeek(false);
            }
        }

        private void DockSlideTo(double y, bool animate = true)
        {
            try
            {
                _dockSlide?.Stop();
                _dockSlide = null;
                if (!animate)
                {
                    DockShift.Y = y;
                    return;
                }
                var anim = new DoubleAnimation
                {
                    To = y,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(anim, DockShift);
                Storyboard.SetTargetProperty(anim, "Y");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                _dockSlide = sb;
                sb.Completed += (_, _) => _dockSlide = null;
                sb.Begin();
            }
            catch { try { DockShift.Y = y; } catch { } }
        }

        private PageKey? _lastDockKey;

        private void SyncDockSelection(PageKey? key, bool first = false)
        {
            if (!_dockBuilt) return;
            Brush? accent = null;
            try { accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush; } catch { }
            bool changed = key != _lastDockKey;
            _lastDockKey = key;
            foreach (var button in DockButtons.Children.OfType<Button>())
            {
                bool on = key.HasValue && button.Tag is PageKey k && k == key.Value;
                try { button.Background = on && accent is not null ? accent : new SolidColorBrush(Microsoft.UI.Colors.Transparent); } catch { }
                if (on && changed && !first) AnimateDockButtonPop(button);
            }
        }

        private Storyboard? _dockPop;

        private void AnimateDockButtonPop(Button button)
        {
            try
            {
                if (button.RenderTransform is not ScaleTransform scale) return;
                _dockPop?.Stop();
                _dockPop = null;
                foreach (var other in DockButtons.Children.OfType<Button>())
                {
                    other.Opacity = 1;
                    if (other.RenderTransform is ScaleTransform otherScale && !ReferenceEquals(other, button))
                    {
                        otherScale.ScaleX = 1;
                        otherScale.ScaleY = 1;
                    }
                }
                scale.ScaleX = 0.82;
                scale.ScaleY = 0.82;
                button.Opacity = 0.45;
                var sb = new Storyboard();
                var sx = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(sx, scale);
                Storyboard.SetTargetProperty(sx, "ScaleX");
                var sy = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(sy, scale);
                Storyboard.SetTargetProperty(sy, "ScaleY");
                var fade = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                };
                Storyboard.SetTarget(fade, button);
                Storyboard.SetTargetProperty(fade, "Opacity");
                sb.Children.Add(sx);
                sb.Children.Add(sy);
                sb.Children.Add(fade);
                sb.Completed += (_, _) => _dockPop = null;
                _dockPop = sb;
                sb.Begin();
            }
            catch { }
        }

        private sealed record DockEntry(PageKey Key, string Tag, string Label, string? Glyph, string? Path, bool SymbolFont);

        private static readonly DockEntry[] DockEntries =
        {
            new(PageKey.Home, "HomePage", "Home", "\uE80F", null, false),
            new(PageKey.Apps, "AppsPage", "Apps", null, Services.NavIcons.AppsPath, false),
            new(PageKey.Graphics, "GraphicsPage", "Graphics", null, Services.NavIcons.GraphicsPath, false),
            new(PageKey.Gaming, "GamingPage", "Affinity", null, Services.NavIcons.AffinityPath, false),
            new(PageKey.Drivers, "DriversPage", "Drivers", "\uE90F", null, false),
            new(PageKey.Uninstaller, "UninstallerPage", "Uninstaller", "\uE74D", null, false),
            new(PageKey.Startup, "StartupPage", "Startup", "\uE7E8", null, false),
            new(PageKey.ThreadTuner, "ThreadTunerPage", "Tuning", null, Services.NavIcons.TuningPath, false),
            new(PageKey.WindowsSettings, "WindowsSettingsPage", "Windows", "\uE713", null, false),
            new(PageKey.PowerPlans, "PowerPlansPage", "Power", "\uE945", null, false),
            new(PageKey.Network, "NetworkPage", "Network", "\uE839", null, true),
            new(PageKey.Input, "InputPage", "Input", "\uE962", null, true),
            new(PageKey.BiosManager, "BiosManagerPage", "BIOS", "\uE9D9", null, false),
            new(PageKey.Snip, "SnipPage", "Snip", "\uE7A8", null, false),
            new(PageKey.Troubleshooting, "TroubleshootingPage", "Fix", "\uE7BA", null, false),
            new(PageKey.Settings, "Settings", "Settings", null, Services.NavIcons.SettingsPath, false),
        };

        private Storyboard? _pageEntrance;

        private static readonly TimeSpan PageFadeDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan PageRiseDuration = TimeSpan.FromMilliseconds(340);

        private void PlayPageEntrance()
        {
            try
            {
                _pageEntrance?.Stop();
                _pageEntrance = null;
                if (ContentFrame.Content is not FrameworkElement page) return;
                var transform = new CompositeTransform();
                page.RenderTransformOrigin = new Point(0.5, 0.5);
                page.RenderTransform = transform;

                var sb = new Storyboard();
                sb.Children.Add(PageAnimation(page, "Opacity", 0, 1, PageFadeDuration));
                sb.Children.Add(PageAnimation(transform, "TranslateY", 24, 0, PageRiseDuration));
                sb.Children.Add(PageAnimation(transform, "ScaleX", 0.986, 1, PageRiseDuration));
                sb.Children.Add(PageAnimation(transform, "ScaleY", 0.986, 1, PageRiseDuration));
                _pageEntrance = sb;
                sb.Completed += (_, _) => _pageEntrance = null;
                sb.Begin();
            }
            catch { }
        }

        private static DoubleAnimation PageAnimation(DependencyObject target, string property, double from, double to, TimeSpan duration)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(duration),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = property != "Opacity",
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            return anim;
        }

        private static readonly TimeSpan RailPopDuration = TimeSpan.FromMilliseconds(240);

        private Storyboard? _railPopStoryboard;
        private NavigationViewItem? _railPopItem;
        private bool _suppressRailPop;

        private void AnimateRailItemPop(NavigationViewItem? item)
        {
            if (item is null || _suppressRailPop)
            {
                return;
            }

            var iconScale = RailIconScale(item);
            var itemShift = RailItemShift(item);
            if (iconScale is null && itemShift is null)
            {
                return;
            }

            if (_railPopStoryboard is not null)
            {
                _railPopStoryboard.Stop();
                _railPopStoryboard = null;
                if (!ReferenceEquals(_railPopItem, item))
                {
                    ResetRailPop(_railPopItem);
                }
            }
            _railPopItem = item;

            if (iconScale is not null)
            {
                iconScale.ScaleX = 1;
                iconScale.ScaleY = 1;
            }
            if (itemShift is not null)
            {
                itemShift.Y = 0;
            }

            var sb = new Storyboard();
            if (iconScale is not null)
            {
                sb.Children.Add(SquishAnimation(iconScale, "ScaleX"));
                sb.Children.Add(SquishAnimation(iconScale, "ScaleY"));
            }
            if (itemShift is not null)
            {
                sb.Children.Add(LiftAnimation(itemShift));
            }

            sb.Completed += (_, _) => _railPopStoryboard = null;

            _railPopStoryboard = sb;
            sb.Begin();
        }

        private static ScaleTransform? RailIconScale(NavigationViewItem item)
        {
            return FindDescendant<FrameworkElement>(item, "IconPresenter")?.RenderTransform as ScaleTransform;
        }

        private static TranslateTransform? RailItemShift(NavigationViewItem item)
        {
            return FindDescendant<FrameworkElement>(item, "ItemStack")?.RenderTransform as TranslateTransform;
        }

        private static void ResetRailPop(NavigationViewItem? item)
        {
            if (item is null)
            {
                return;
            }

            if (RailIconScale(item) is ScaleTransform scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
            if (RailItemShift(item) is TranslateTransform shift)
            {
                shift.Y = 0;
            }
        }

        private static DoubleAnimationUsingKeyFrames SquishAnimation(ScaleTransform target, string property)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(RailPopDuration),
                EnableDependentAnimation = true,
            };

            anim.KeyFrames.Add(SquishKeyFrame(0.00, 1.00, 0.1, 0.0, 0.4, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.30, 0.82, 0.3, 0.0, 0.2, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.68, 1.10, 0.2, 0.0, 0.3, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(1.00, 1.00, 0.4, 0.0, 0.6, 1.0));

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            return anim;
        }

        private static DoubleAnimationUsingKeyFrames LiftAnimation(TranslateTransform target)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(RailPopDuration),
                EnableDependentAnimation = true,
            };

            anim.KeyFrames.Add(SquishKeyFrame(0.00, 0.0, 0.1, 0.0, 0.4, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.30, -3.0, 0.3, 0.0, 0.2, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.70, 0.8, 0.2, 0.0, 0.3, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(1.00, 0.0, 0.4, 0.0, 0.6, 1.0));

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, "Y");
            return anim;
        }

        private static SplineDoubleKeyFrame SquishKeyFrame(double at, double value, double x1, double y1, double x2, double y2)
        {
            var spline = new KeySpline
            {
                ControlPoint1 = new Point(x1, y1),
                ControlPoint2 = new Point(x2, y2),
            };

            return new SplineDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromTicks((long)(RailPopDuration.Ticks * at)),
                Value = value,
                KeySpline = spline,
            };
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Name == name)
                {
                    return match;
                }

                var found = FindDescendant<T>(child, name);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
