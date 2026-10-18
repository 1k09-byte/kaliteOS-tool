// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
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
                        DispatcherQueue.TryEnqueue(ShowMainWindow);
                    }
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
            try { (Application.Current as App)?.GamingMode.ReleaseAll(); } catch { }
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
                    var live = App.Services.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                    bool active = live is not null && (live.State == kaliteConfig.Services.LiveWallpaper.LiveState.Playing || live.State == kaliteConfig.Services.LiveWallpaper.LiveState.Paused);
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
                        var live = App.Services.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                        if (live?.State == kaliteConfig.Services.LiveWallpaper.LiveState.Playing) live.Pause();
                        else if (live?.State == kaliteConfig.Services.LiveWallpaper.LiveState.Paused) live.Resume();
                    }
                    catch { }
                });
            };
            _tray.OnStopLive += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var live = App.Services.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
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
                    try { (Application.Current as App)?.GamingMode.ReleaseAll(); } catch { }
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
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(8));
                    var live = App.Services.GetService(typeof(kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine)) as kaliteConfig.Services.LiveWallpaper.ILiveWallpaperEngine;
                    if (live is not null && live.State == kaliteConfig.Services.LiveWallpaper.LiveState.Stopped)
                        await live.TryResumeOnLaunchAsync();
                }
                catch { }
            });
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
            args.Cancel = true;
            EnsureTrayShown();
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
                        AppWindow.Hide();
                    }
                }
                catch { }
            };
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            WatchMaterialChanges();
            this.Activated += (_, _) => GuardedActivation(() => WatchMaterialChanges());

            NavShell.LayoutUpdated += (_, _) => UpdateNavPill();

            var appsItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == "AppsPage");
            _suppressRailPop = true;
            if (appsItem != null)
                NavView.SelectedItem = appsItem;
            else if (NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault() is NavigationViewItem first)
                NavView.SelectedItem = first;
            _suppressRailPop = false;

            NavView.Loaded += async (_, _) => 
            {
                SuppressSidebarTooltips();
                if (!App.IsAdmin && !App.StartedToTray)
                {
                    await ShowElevationDialogAsync();
                }
                await TryRunKaliteOSAutoSetupAsync();
            };
            this.Activated += (_, _) => { GuardedActivationAsync(TryRunKaliteOSAutoSetupAsync); };
            SuppressSidebarTooltips();
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

        private async void GuardedActivationAsync(Func<Task> work)
        {
            try
            {
                await work();
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

        private async Task ShowElevationDialogAsync()
        {
            try
            {
                var xamlRoot = this.Content?.XamlRoot;
                if (xamlRoot == null) return;
                var dialog = new ContentDialog
                {
                    Title = "Administrator Rights Required",
                    Content = "This application requires advanced access to modify kernel-level thread affinities, power plans, and OS telemetry. Please restart as Administrator.",
                    PrimaryButtonText = "Restart as Admin",
                    CloseButtonText = "Continue anyway",
                    XamlRoot = xamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = System.Environment.ProcessPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    try
                    {
                        System.Diagnostics.Process.Start(processInfo);
                    }
                    catch { return; }
                    Application.Current.Exit();
                }
            }
            catch { }
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

        private void UpdateNavPill(NavigationViewItem? item = null)
        {
            item ??= NavView.SelectedItem as NavigationViewItem;
            if (item is null || item.ActualHeight <= 0 || NavShell.ActualHeight <= 0)
            {
                return;
            }

            var pos = item.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double top = pos.Y + ((item.ActualHeight - NavPill.Height) / 2);

            const double pillInset = 0;

            var box = RailItemBody(item);
            if (box is null || box.ActualWidth <= 0)
            {
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

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var nav = App.Services.GetService(typeof(INavigationService)) as INavigationService;
            nav?.Attach(ContentFrame);
            if (args.IsSettingsSelected)
            {
                nav?.Navigate(PageKey.Settings);
                UpdateNavPill();
                AnimateRailItemPop(args.SelectedItem as NavigationViewItem);
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                nav?.Navigate(NavigationService.KeyForTag(tag));
                UpdateNavPill(item);
                AnimateRailItemPop(item);
            }
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

        private bool _kaliteOSAutoSetupRan;

        private async Task TryRunKaliteOSAutoSetupAsync()
        {
            if (_kaliteOSAutoSetupRan) return;

            int isInstalled;
            try { isInstalled = Services.KaliteOSRegistryService.GetIsInstalled(); }
            catch { return; }

            if (isInstalled == 1)
            {
                _kaliteOSAutoSetupRan = true;
                return;
            }

            if (KaliteOSStartupOverlay == null || KaliteOSProgressBar == null) return;

            if (_kaliteOSAutoSetupRan) return;
            _kaliteOSAutoSetupRan = true;

            await Task.Delay(600);

            DispatcherQueue.TryEnqueue(() =>
            {
                KaliteOSStartupOverlay.Visibility = Visibility.Visible;
                KaliteOSProgressBar.IsIndeterminate = true;
                KaliteOSProgressBar.Value = 0;
                KaliteOSProgressText.Text = "Preparing Windhawk installation…";
                KaliteOSPercentText.Text = "";
                KaliteOSPercentText.Visibility = Visibility.Collapsed;
            });

            var vm = new ViewModels.WindhawkProvisioningViewModel();
            vm.RefreshDetection();

            void UpdateFromVm()
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    string status = vm.StatusText;
                    if (string.IsNullOrWhiteSpace(status))
                        status = vm.IsBusy ? "Working…" : "Preparing…";
                    KaliteOSProgressText.Text = status;

                    var pct = vm.DownloadPercent;
                    if (pct.HasValue)
                    {
                        KaliteOSProgressBar.IsIndeterminate = false;
                        KaliteOSProgressBar.Value = Math.Clamp(pct.Value, 0, 100);
                        KaliteOSPercentText.Text = $"{pct.Value:F0}%";
                        KaliteOSPercentText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        KaliteOSProgressBar.IsIndeterminate = vm.IsBusy;
                        if (!vm.IsBusy) KaliteOSProgressBar.Value = 100;
                        KaliteOSPercentText.Visibility = Visibility.Collapsed;
                    }
                });
            }

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.StatusText) || e.PropertyName == nameof(vm.DownloadPercent) || e.PropertyName == nameof(vm.IsBusy))
                    UpdateFromVm();
            };
            UpdateFromVm();

            try
            {
                if (!Services.WindhawkDetectionService.IsRunningElevated())
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        KaliteOSProgressBar.IsIndeterminate = false;
                        KaliteOSProgressText.Text = "Administrator rights are required to install Windhawk (it installs a system service). Please restart as Administrator - auto-setup will retry on next elevated launch.";
                        KaliteOSProgressBar.Value = 0;
                    });
                    await Task.Delay(5000);
                    DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
                    return;
                }

                await vm.RunProvisioningAsync();

                bool installed = false;
                try { installed = vm.Installation.IsInstalled && vm.Installation.CliPath != null; } catch { }
                if (!installed)
                {
                    try { installed = Services.KaliteOSRegistryService.GetIsInstalled() == 1; } catch { }
                    if (vm.Installation.IsInstalled) installed = true;
                }

                if (installed)
                {
                    try { Services.KaliteOSRegistryService.SetIsInstalled(1); } catch { }
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        KaliteOSProgressBar.IsIndeterminate = false;
                        KaliteOSProgressBar.Value = 100;
                        KaliteOSProgressText.Text = "Done - Windhawk installed and KaliteOS mods imported.";
                        KaliteOSPercentText.Text = "100%";
                        KaliteOSPercentText.Visibility = Visibility.Visible;
                    });
                    await Task.Delay(2200);
                    DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        KaliteOSProgressBar.IsIndeterminate = false;
                        string msg = vm.HasMessage ? vm.Message : "Windhawk provisioning finished but verification failed.";
                        KaliteOSProgressText.Text = msg + " Will retry on next launch (IsInstalled stays 0).";
                    });
                    await Task.Delay(6000);
                    DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
                }
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    KaliteOSProgressBar.IsIndeterminate = false;
                    KaliteOSProgressText.Text = $"Auto-setup failed: {ex.Message} - will retry on next launch.";
                    KaliteOSPercentText.Visibility = Visibility.Collapsed;
                });
                await Task.Delay(6000);
                DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
            }
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
