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
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private kaliteConfig.Services.TrayIconService? _tray;
        private bool _allowExit;
        private bool _trayShown;

        /// <summary>
        /// Restores the main window from the tray (shared by the tray Open
        /// action and the second-instance signal below).
        /// </summary>
        public void ShowMainWindow()
        {
            AppWindow.Show();
            var hwnd = WindowNative.GetWindowHandle(this);
            _ = kaliteConfig.Services.TrayIconService.TrayForeground.BringToFront(hwnd);
        }

        /// <summary>
        /// Retries Shell_NotifyIcon on a short timer until the icon sticks.
        /// Bounded so a genuinely unavailable tray (no Explorer, missing .ico)
        /// costs nothing: after the last attempt the timer is dropped and the
        /// next close/minimize will try again.
        /// </summary>
        // Top inset for the full-height nav shell.
        // The shell runs under the floating title bar.
        // Starts the NavigationView below the caption strip.
        // Driven by the TitleBar's real height.
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

        /// <summary>
        /// Best-effort (re)registration of the tray icon, so Close/Minimize
        /// can always park there even if the startup registration failed.
        /// </summary>
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

        /// <summary>
        /// Named signal a second instance (Start menu / desktop launch while
        /// the tray instance runs) sets to ask this instance to show its
        /// window instead of starting a duplicate.
        /// </summary>
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

        /// <summary>
        /// Destroys the hidden tray window and removes the notification icon.
        ///
        /// Called first thing during app teardown. The tray window is native
        /// state that outlives the managed graph: Windows keeps delivering
        /// messages to it for as long as it exists, so it must be gone before
        /// the runtime starts tearing managed state down - not merely dropped.
        /// Safe to call when the tray was already disposed by an exit path.
        /// </summary>
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

        /// <summary>
        /// Stops tray intercepts and releases hooks before a silent in-place
        /// upgrade (Inno Setup must replace the running exe).
        /// </summary>
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

            // The tint layer owns its own brush (never a shared theme resource),
            // so the saved colour can be swapped in without re-resolving
            // anything or fighting a theme-dictionary swap.
            // The rail mask owns its brush too: its colour is derived from the
            // tint rather than hard-coded, so the tint reaches the chrome.
            NavRailMask.Background = _railMaskBrush;
            // Caption band brush (built here, not XAML: the colour has to
            // track the tint-blended chrome, which only exists at runtime).
            //
            // BOTH stops carry the chrome colour. The wash used to ramp from
            // chrome to fully transparent so there would be no seam under the
            // caption strip, but with a backdrop material active (Acrylic /
            // Mica render BEHIND the XAML) every transparent pixel in that band
            // let the material show through, and the title bar grew a bright
            // line across it - worst at the bottom edge of the band, exactly
            // where the ramp reached zero alpha. It only showed up with no
            // background image, because a chosen image is painted RowSpan=2
            // and covers the material, hiding the leak.
            //
            // So the band is opaque chrome end to end, which is also what the
            // nav rail does (NavRailMask). The band/page boundary underneath is
            // a small dark step rather than a bright band across the title bar,
            // and it matches the rail-to-page edge the app already ships.
            _titleFadeBrush.StartPoint = new Windows.Foundation.Point(0, 0);
            _titleFadeBrush.EndPoint = new Windows.Foundation.Point(0, 1);
            _titleFadeBrush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0 });
            _titleFadeBrush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 });
            TitleFade.Background = _titleFadeBrush;
            _backdropTintBrush = new SolidColorBrush();
            TintLayer.Background = _backdropTintBrush;
            ApplyBackgroundImage();
            ApplyBackdropTint(kaliteConfig.Services.BackdropTint.Load());
            // Content spans the full window under the floating title bar:
            // keep the nav shell clear of the caption-button strip using
            // the TitleBar's measured height, not a guessed constant.
            try
            {
                titleBar.SizeChanged += (_, _) => SyncNavTopInset();
                SyncNavTopInset();
            }
            catch { }

            // Explicitly request Windows 11 rounded window corners (the green-check
            // shape in the reference) instead of inheriting whatever the default
            // resolves to with a custom title bar + Mica backdrop.
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                int preference = DWMWCP_ROUND;
                kaliteConfig.Native.DwmApi.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Cosmetic only: a square window is still fully usable.
            }

            // Draw the app's own TitleBar control into the caption area.
            // Without this the window falls back to the classic Windows
            // system title bar - the plain "kaliteConfig" text plus the
            // Minimize/Maximize/Close buttons, on its own opaque bar, with
            // the app's XAML TitleBar (and its drag region, search box and
            // caption buttons) sitting unused below it. That is the old
            // looking bar; the XAML <TitleBar> is only drawn into the
            // window when content is extended into the title bar.
            ExtendsContentIntoTitleBar = true;

            // Keep the three-pane BIOS layout usable: below ~1100px the detail
            // pane collapses, so this floor prevents accidental crushing.
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                try { presenter.PreferredMinimumWidth = 1100; }
                catch { /* older Windows App SDK: cosmetic only */ }
            }
            AppWindow.Title = "kaliteConfig";
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { } // icon is cosmetic; never block startup

            // Close button minimizes to the system tray instead of exiting.
            // Exit only via the tray menu (or Settings, which calls AllowExit+Close).
            _tray = new kaliteConfig.Services.TrayIconService();
            _tray.OnOpen += () =>
            {
                DispatcherQueue.TryEnqueue(ShowMainWindow);
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
                            // Optionally show notification
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

            // The icon must stay in the tray until the user quits explicitly,
            // so a failed Shell_NotifyIcon cannot be left to fail forever.
            //
            // On a logon start this genuinely happens: Explorer is still
            // building the notification area, the first NIM_ADD is rejected, and
            // because _trayShown latched false nothing ever tried again. The
            // window would then park to a tray that had no icon in it. Retry on
            // a short timer until it sticks.
            if (!_trayShown)
                ScheduleTrayRetry();
            // Second-instance handoff: a Start menu / desktop launch while
            // this tray instance runs signals us to show the window.
            StartShowWindowListener();
            // Persistent active-game-profile indicator (overclock v2 Part B):
            // the user is usually in-game - not looking at the app - when an
            // auto-switch fires, so the tray tooltip carries the active
            // profile name. The app already minimizes to this tray icon.
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
            catch { } // cosmetic only; auto-switch works without the tooltip
        AppWindow.Closing += (_, args) =>
        {
            // Close always parks to the tray (never exits): the rules engine
            // lives in-process, so exiting would stop all rules. Exit only
            // via the tray menu or Settings (AllowExitAndClose).
            if (_allowExit) return;
            args.Cancel = true;
            EnsureTrayShown();
            AppWindow.Hide();
        };
            // Minimize button also parks to the tray instead of the taskbar.
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
            // The ThemeService is initialized in App.OnLaunched after this
            // constructor; retry the subscription once the window activates
            // (and again on first layout) so it always lands.
            this.Activated += (_, _) => GuardedActivation(() => WatchMaterialChanges());

            // Default to the Apps (installer/packages/uninstaller) page on launch.
            // Pill position is layout-driven (LayoutUpdated): event-driven updates
            // (SelectionChanged/SizeChanged) can compute against a stale layout
            // pass when maximizing/restoring, stranding the pill on the wrong item.
            NavShell.LayoutUpdated += (_, _) => UpdateNavPill();

            // Lock state first: overlay defaults to Visible in XAML so the first
            // frame is already covered; collapse it now when verified, before any
            // navigation triggers entrance transitions.
            EnforceVerificationLock();

            // Default to the Apps (installer/packages/uninstaller) page on launch.
            // Skipped entirely when locked: no page ever loads behind the overlay.
            if (!App.VerificationFailed)
            {
                var appsItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (i.Tag as string) == "AppsPage");
                // The launch-time selection is not a user navigation, so the rail
                // pop is suppressed for it - the app would otherwise spring the
                // first icon before the window is even on screen.
                _suppressRailPop = true;
                if (appsItem != null)
                    NavView.SelectedItem = appsItem;
                else if (NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault() is NavigationViewItem first)
                    NavView.SelectedItem = first;
                _suppressRailPop = false;
            }

            NavView.Loaded += async (_, _) => 
            {
                if (App.VerificationFailed) return;
                SuppressSidebarTooltips();
                // Silent login start (tray): never pop a modal over a hidden
                // window - admin status is also surfaced in Settings.
                if (!App.IsAdmin && !App.StartedToTray)
                {
                    await ShowElevationDialogAsync();
                }
                await TryRunKaliteOSAutoSetupAsync();
            };
            // Window has no Loaded event (WinUI 3) - also schedule via Activated so auto-setup
            // is not missed if NavView is already loaded before we subscribe.
            // Never scheduled when locked (TryRunKaliteOSAutoSetupAsync also guards).
            this.Activated += (_, _) => { if (!App.VerificationFailed) GuardedActivationAsync(TryRunKaliteOSAutoSetupAsync); };
            SuppressSidebarTooltips();
        }

        private bool _materialWatchAttached;

        /// <summary>
        /// Keeps the root background in sync with the material setting:
        /// opaque theme background when Material = None (otherwise the raw
        /// black window surface shows through the transparent nav pane),
        /// transparent when a backdrop is active (backdrops render behind the
        /// XAML content - an opaque root would cover them).
        ///
        /// NOTE: the ThemeService is created in App.OnLaunched AFTER this
        /// window's constructor runs, so an early subscribe attempt would see
        /// null and bail forever. Subscribe lazily: try on every call, and
        /// once the service exists attach the handlers (idempotent).
        /// </summary>
        /// <summary>
        /// Runs window-activation work so it can never terminate the process.
        ///
        /// Both activation handlers below do native work (Mica/Acrylic background
        /// material via DWM, and KaliteOS auto-setup provisioning). Activation
        /// fires every time the window regains focus, which is exactly what
        /// happens when the user returns from another application.
        /// Unguarded, a failure there escapes as an unhandled exception on the
        /// dispatcher and kills the app with a stackless fatal error, with no
        /// crash-log entry. Neither operation is important enough to justify
        /// taking the window down, so both are contained here.
        /// </summary>
        private void GuardedActivation(Action work)
        {
            try
            {
                work();
            }
            catch { /* decorative/theming work must never break activation */ }
        }

        private async void GuardedActivationAsync(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch { /* provisioning must never break activation */ }
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
                // Service appeared between our last apply and now - re-apply
                // so a persisted non-None material is honored at startup.
                OnThemeChanged();
            }
            catch { }

        }

        /// <summary>
        /// Keeps the root background in sync with the material setting.
        ///
        /// The root is left transparent in BOTH cases, which is what makes
        /// Material = None read as full black:
        ///
        ///  - No backdrop: with <c>Window.SystemBackdrop</c> null there is no
        ///    backdrop to render, and the raw window surface behind the XAML is
        ///    black. Painting <c>ApplicationPageBackgroundThemeBrush</c> here
        ///    would replace that with WinUI's dark gray (#202020) - the app stops
        ///    being black. AutoOS works the same way: its RootGrid only ever gets
        ///    a brush when the user picked a Tint Color, otherwise nothing paints
        ///    over the window surface.
        ///  - Backdrop active: backdrops render BEHIND the XAML content, so an
        ///    opaque root would hide them entirely.
        /// </summary>
        /// <summary>
        /// Paints the user's backdrop tint over every surface of the window:
        ///
        ///  - the page field gets a translucent wash over the backdrop, so the
        ///    material still reads through it (alpha 0 = nothing there);
        ///  - the chrome - nav rail, caption band and the NavigationView's own
        ///    pane fills - gets the SAME tint blended into the matte surface they
        ///    are painted with. They stay opaque on purpose: the meteor backdrop
        ///    drifts behind the whole window, and a translucent rail let its
        ///    streaks smear through the icons and labels. Blending keeps them
        ///    opaque while still carrying the tint, so the colour reaches the
        ///    rail and the title bar too.
        ///
        /// Called at startup and from Settings the moment the colour or the
        /// strength moves.
        /// </summary>
        public void ApplyBackdropTint((byte R, byte G, byte B, byte A) tint)
        {
            if (_backdropTintBrush is null) return;
            bool isLight = RootGrid.ActualTheme == ElementTheme.Light;
            // Light-mode damper: the same tint strength that reads as a wash
            // over dark matte floods near-white surfaces and takes over the
            // whole theme. Scale the alpha down (40%) so light mode keeps a
            // whisper of the hue. Everything downstream - overlay wash,
            // SurfaceTint, rail washes - keys off this one value.
            if (isLight)
                tint = (tint.R, tint.G, tint.B, (byte)(tint.A * 0.4));
            _backdropTintBrush.Color = Windows.UI.Color.FromArgb(tint.A, tint.R, tint.G, tint.B);
            // Surfaces first: this recolors the shared card brushes in place.
            kaliteConfig.Services.SurfaceTint.Apply(tint, isLight);

            // ONE COLOUR FOR THE WHOLE WINDOW.
            //
            // The page background brush is Transparent in this theme, so the
            // page area shows the window's own backdrop - the material, the
            // meteor field, or the chosen picture. The side nav and the title
            // bar used to paint their own opaque colours on top of that, and
            // because they were three different values (rail card colour,
            // caption matte, page backdrop) the window read as a stack of
            // boxes in mismatched shades rather than one surface.
            //
            // They now paint nothing at all: rail mask collapsed, pane fills
            // clear, caption band clear. All three areas therefore show the
            // SAME backdrop by construction - with a picture it reaches every
            // edge, without one the meteor field drifts continuously through
            // the nav and the caption strip - and there is no opaque band for
            // the backdrop material to brighten into a line.
            NavRailMask.Visibility = Visibility.Collapsed;
            var paneColor = Microsoft.UI.Colors.Transparent;
            if (_titleFadeBrush.GradientStops.Count >= 2)
            {
                _titleFadeBrush.GradientStops[0].Color = Microsoft.UI.Colors.Transparent;
                _titleFadeBrush.GradientStops[1].Color = Microsoft.UI.Colors.Transparent;
            }
            var rail = NavView.Resources;
            if (rail is null) return;
            // The rail paints from the NavigationView's own fill.
            if (rail["NavigationViewBackground"] is SolidColorBrush navBackground)
                navBackground.Color = paneColor;
            if (rail["NavigationViewDefaultPaneBackground"] is SolidColorBrush paneBackground)
                paneBackground.Color = paneColor;
            if (rail["NavigationViewExpandedPaneBackground"] is SolidColorBrush expandedPaneBackground)
                expandedPaneBackground.Color = paneColor;
            // Item plates carry the tint hue, never a fixed ink. They live in the
            // theme dictionaries now (per-theme Light/Dark washes), so the tint
            // writes into application resources where ThemeResource lookups
            // resolve - not into NavView.Resources, which no longer owns them.
            // No tint chosen yet keeps the theme's neutral wash: white ink on
            // dark, black ink on light (from RootGrid's live theme, never a
            // hardcoded assumption about which theme is active).
            byte ink = isLight ? (byte)0x00 : (byte)0xFF;
            byte wr = tint.A > 0 ? tint.R : ink;
            byte wg = tint.A > 0 ? tint.G : ink;
            byte wb = tint.A > 0 ? tint.B : ink;
            SetRailWash("RailHighlightSelectedBrush", 0x4D, wr, wg, wb);
            SetRailWash("RailHighlightHoverBrush", 0x38, wr, wg, wb);
            SetRailWash("RailHighlightPressedBrush", 0x2E, wr, wg, wb);
        }
        // Writes one rail wash brush into the ACTIVE theme's instance (the one
        // live plates resolve). A plain root lookup would serve the dark entry
        // and paint off-screen in light mode.
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
        // Live card color from the ACTIVE theme's dictionary instance (the one
        // live chrome resolves). Plain root TryGetValue serves the dark
        // Default entry no matter the theme - the stuck-dark rail in light
        // mode. Tint included: SurfaceTint mutates these instances in place.
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



        // ── background image helpers ─────────────────────────────────────────────

        /// <summary>Window width in physical pixels, if the window is available.
        /// Falls back to null so the caller can use a safe default.
        /// </summary>
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

        /// <summary>Window aspect (width/height) in physical pixels, if available.
        /// </summary>
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

        /// <summary>Decoded aspect (width/height) of a BitmapImage, if its dimensions are
        /// already populated. Returns null when the image has not decoded yet or the
        /// dimensions are unavailable.
        /// </summary>
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

        /// <summary>
        /// Applies the user's background-image preference (file path, opacity,
        /// stretch) behind the tint layer but above the meteor field. When there
        /// is no saved image, the layer is hidden and the meteor field shows
        /// through.
        ///
        /// The image is decoded on the UI thread (BitmapImage has UI-thread
        /// affinity). File I/O is async, but the decode + assignment happens here
        /// because this method is called on the UI thread at startup and from
        /// settings changes.
        /// </summary>
        public void ApplyBackgroundImage()
        {
            LoadBackgroundImage();
            // The rail chrome follows the image (open rail vs matte rail),
            // so refresh it after the layer visibility settles.
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

            // Best-effort: a missing or unreadable file is treated as "no image"
            // rather than breaking the window.
            if (!System.IO.File.Exists(pref.Path))
            {
                BgImageLayer.Visibility = Visibility.Collapsed;
                BgImage.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                // Decode at screen resolution (full quality for display), capped so a
                // 4K/ultrawide wallpaper cannot blow up memory. A width at or above the
                // window's pixel width keeps the image crisp on the backdrop.
                var winW = (int?)MaybeWindowPixelWidth();
                var decodeW = winW.HasValue ? Math.Max(256, Math.Min(winW.Value, 2560)) : Math.Max(256, 1920);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelWidth = decodeW;
                bmp.UriSource = new Uri(pref.Path);

                // Choose the best-looking fit when the user picked Auto. The rule is the
                // standard "cover with no letterbox" behaviour: keep the image filling edge
                // to edge, and prefer UniformToFill (cover) unless the image is so tall
                // that cover would crop an unreasonable share of width, in which case fit
                // inside (Uniform) and let side margins show (the meteor field shows through
                // the margins, so this is intentional, not a defect).
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

        /// <summary>
        /// Applies only the opacity, without touching the bitmap.
        ///
        /// ApplyBackgroundImage re-decodes the file from disk on every call, so
        /// driving it from the opacity slider meant a full decode + flicker on
        /// every value change - and the slider handler avoided calling it
        /// altogether, so dragging it moved the label and rewrote the store while
        /// the window never repainted. That is the whole bug: the value was being
        /// saved correctly and simply never read back until something else
        /// happened to reload the image.
        ///
        /// BgImageLayer is the transparent host that gives the layer its hit-test
        /// and z-order slot, so only the Image itself carries the opacity.
        /// </summary>
        public void ApplyBackgroundImageOpacity(byte opacityPercent)
        {
            // Clamp in int, not byte: Math.Clamp is overloaded for both and a
            // byte argument makes the call ambiguous.
            BgImage.Opacity = Math.Clamp((int)opacityPercent, 0, 100) / 100.0;
        }

        /// <summary>
        /// Keeps the root background in sync with the material setting, and re-
        /// applies the tint after a theme rewrite. A theme switch replaces the
        /// shared surface brushes with the new theme's values, so the captured
        /// bases are dropped first - otherwise the tint would compound on top of
        /// the already-tinted colour with every switch.
        /// </summary>
        private void OnThemeChanged()
        {
            // Opaque root only when there is no system backdrop.
            // None material shows the desktop through transparent pixels.
            // Dark gets black, light gets white, from the live theme.
            // A material keeps transparency so Mica and Acrylic render.
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
                if (xamlRoot == null) return; // too early to show UI; status is also in Settings
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
                    catch { return; } // User cancelled UAC: stay in this instance
                    Application.Current.Exit();
                }
            }
            catch { } // never crash startup over an advisory dialog
        }

        private void SuppressSidebarTooltips()
        {
            foreach (var item in System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.MenuItems)
                .Concat(System.Linq.Enumerable.OfType<NavigationViewItem>(NavView.FooterMenuItems)))
            {
                ToolTipService.SetToolTip(item, new ToolTip { Visibility = Visibility.Collapsed });
            }
            // Compact rail must never show a scrollbar: hide the internal
            // scrollers' bars (wheel still works as a fallback on tiny windows).
            HideNavScrollbars(NavView);
        }

        /// <summary>
        /// The rail item's plate, i.e. the <c>ItemBody</c> border inside
        /// RailNavItemStyle's template.
        ///
        /// Memoised per item because this runs on NavShell.LayoutUpdated - a
        /// layout pass happens on plenty of things a pointer sweep sets off, and
        /// FindDescendant is a recursive visual-tree walk, so doing it every pass
        /// meant walking the selected item's whole subtree continuously while the
        /// user moved along the rail. A template is not re-realised while the item
        /// stays alive, so one lookup per item is enough.
        /// </summary>
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

        /// <summary>
        /// Moves the shared <c>NavPill</c> to the vertical center of the selected
        /// rail item. Runs on every layout pass but only assigns when the target
        /// actually moved (epsilon guard), so the assignment itself can't cause a
        /// layout loop. No-ops before first layout.
        /// </summary>
        private void UpdateNavPill(NavigationViewItem? item = null)
        {
            item ??= NavView.SelectedItem as NavigationViewItem;
            if (item is null || item.ActualHeight <= 0 || NavShell.ActualHeight <= 0)
            {
                return;
            }

            var pos = item.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double top = pos.Y + ((item.ActualHeight - NavPill.Height) / 2);

            // The 3px pill aligns flush with the highlight's left edge; its
            // 18px height remains centered in the item cell.
            const double pillInset = 0;

            var box = RailItemBody(item);
            if (box is null || box.ActualWidth <= 0)
            {
                return;
            }

            // A glide already in flight lands on the slot it started for, so
            // retargeting here would restart it on every layout pass of the
            // incoming page. Let it finish instead; the completion callback
            // re-runs this method and corrects any residual drift in one short
            // follow-up glide.
            if (_navPillGlide is not null)
            {
                return;
            }

            var boxPos = box.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double left = boxPos.X + pillInset;

            // Where the pill is actually PAINTED, which is the layout slot plus
            // the live travel offset. Comparing that (not just the slot) is what
            // makes the guard idempotent: once the glide settles at zero, every
            // later layout pass sees no delta and does nothing.
            double renderedLeft = NavPill.Margin.Left + NavPillShift.X;
            double renderedTop = NavPill.Margin.Top + NavPillShift.Y;

            bool needsMove = Math.Abs(renderedTop - top) > 0.5 || Math.Abs(renderedLeft - left) > 0.5;
            bool firstPlacement = !_navPillPlaced;

            if (!needsMove && !firstPlacement)
            {
                return;
            }

            // Travel distance for the glide: the painted position relative to the
            // NEW slot. Reading it live (rather than diffing two slots) is what lets
            // a re-target mid-glide continue smoothly from the current offset.
            double offsetY = renderedTop - top;
            double offsetX = renderedLeft - left;

            // Layout slot jumps instantly; only the offset above is animated, so the
            // pill appears to travel rather than teleport.
            NavPill.Margin = new Thickness(left, top, 0, 0);
            AnimateNavPillToRest(offsetX, offsetY, firstPlacement);
        }

        private Storyboard? _navPillGlide;
        private bool _navPillPlaced;

        /// <summary>
        /// Animates <c>NavPillShift</c> from the supplied offset back to (0,0) so the
        /// pill glides from where it currently sits to the slot its Margin was just
        /// snapped to. Opacity dips slightly for the duration of the travel, which
        /// makes a long jump across the rail legible as movement rather than a jump,
        /// and settles back to fully opaque on arrival.
        /// </summary>
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

            // The very first placement has no previous row to travel from, so it
            // only fades in - gliding from the window origin would read as the pill
            // flying in from nowhere on launch.
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

            // Completed, not a timer: if the storyboard is stopped early the
            // callback never runs, which is exactly when we do NOT want the
            // handler to think the pill has settled.
            sb.Completed += (_, _) =>
            {
                _navPillGlide = null;
                NavPillShift.X = 0;
                NavPillShift.Y = 0;
                // Re-check: the row may have moved while we were travelling
                // (page load, pane resize). Any drift now resolves in one
                // short follow-up glide instead of leaving the pill stranded.
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

        // ---------------------------------------------------------------------
        // RAIL SELECTION ANIMATION
        //
        // The rail item template (RailNavItemStyle in MainWindow.xaml) exposes
        // two animated transforms: IconScale (uniform scale about the glyph
        // centre) and ItemShift (vertical translate of the icon+caption cell).
        // They are driven from here rather than from a VisualState because a
        // VisualState can only ease between two static values - it cannot
        // express "squash, then overshoot past rest, then settle", which is the
        // shape that reads as a springy pop.
        // ---------------------------------------------------------------------

        private static readonly TimeSpan RailPopDuration = TimeSpan.FromMilliseconds(240);

        private Storyboard? _railPopStoryboard;
        private NavigationViewItem? _railPopItem;
        private bool _suppressRailPop;

        /// <summary>
        /// Plays the rail selection animation on <paramref name="item"/>: the icon
        /// squashes toward 0.82, springs past rest to 1.10 and settles back to 1,
        /// while the caption lifts ~3px and settles. Runs on every rail selection
        /// change, including a rapid second click - in that case the in-flight pop
        /// is dropped back to rest on the previous item first, so no icon can be
        /// stranded mid-squish.
        /// </summary>
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

            // Abort any pop still in flight. If it was a different item, snap that
            // one back to rest so its icon is not left frozen between keyframes.
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

            // Re-assert the start values: a stop mid-flight leaves the transform on
            // whatever keyframe it was on, and the storyboard's first keyframe needs
            // to begin from a known state.
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

        /// <summary>
        /// Reads a rail item's animated transforms out of its template. Transforms are
        /// not visual-tree children, so the usual visual walk cannot reach them: the
        /// owning element is found by name and its RenderTransform is unwrapped.
        /// Both return null for an item that does not use RailNavItemStyle, which
        /// every caller already tolerates.
        /// </summary>
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

        /// <summary>
        /// The squish curve, as keyframes: rest -> compressed -> slight overshoot ->
        /// rest. SplineDoubleKeyFrame is required over Discrete so the segments are
        /// interpolated; each segment carries its own KeySpline, because a single
        /// easing function cannot both rush into the squash and ease out of the
        /// settle.
        /// </summary>
        private static DoubleAnimationUsingKeyFrames SquishAnimation(ScaleTransform target, string property)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(RailPopDuration),
                EnableDependentAnimation = true,
            };

            // Fractions of the total 240ms: compress by ~70ms, spring out by ~92ms,
            // settle in the remaining ~78ms.
            anim.KeyFrames.Add(SquishKeyFrame(0.00, 1.00, 0.1, 0.0, 0.4, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.30, 0.82, 0.3, 0.0, 0.2, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(0.68, 1.10, 0.2, 0.0, 0.3, 1.0));
            anim.KeyFrames.Add(SquishKeyFrame(1.00, 1.00, 0.4, 0.0, 0.6, 1.0));

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            return anim;
        }

        /// <summary>
        /// The caption's lift: up and back past its rest position a touch before
        /// settling, mirroring the icon's overshoot so the two read as one motion.
        /// </summary>
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
            // WinUI's KeySpline has no (x1,y1,x2,y2) constructor; the control points
            // are settable Point properties.
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

        /// <summary>
        /// KaliteOS first-launch auto-provisioning: checks HKLM\SOFTWARE\KaliteOS IsInstalled.
        /// 0 (or missing) → show progress overlay and silently install Windhawk + import bundled KaliteOS mods.
        /// 1 → do nothing. On success, writes IsInstalled=1 so next launch is a no-op.
        /// Progress is surfaced via the startup overlay's determinate/indeterminate ProgressBar and status TextBlocks.
        /// </summary>
        private async Task TryRunKaliteOSAutoSetupAsync()
        {
            if (App.VerificationFailed) return;
            if (_kaliteOSAutoSetupRan) return;

            int isInstalled;
            try { isInstalled = Services.KaliteOSRegistryService.GetIsInstalled(); }
            catch { return; }

            if (isInstalled == 1)
            {
                _kaliteOSAutoSetupRan = true; // no-op: mark done so we don't re-check
                return;
            }

            // Must have overlay/controls available - if not yet loaded, defer (don't mark ran)
            if (KaliteOSStartupOverlay == null || KaliteOSProgressBar == null) return;

            // Only mark as started after we know we need to run and controls exist
            if (_kaliteOSAutoSetupRan) return;
            _kaliteOSAutoSetupRan = true;

            // Small delay so window layout settles before overlay appears
            await Task.Delay(600);

            // Show overlay
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
            // Prime detection
            vm.RefreshDetection();

            // Bridge ViewModel progress -> overlay
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
                        // During Installing/Importing phases DownloadPercent is null → indeterminate
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
                // If not elevated, provisioning will fail with UnauthorizedAccessException.
                // Surface that clearly via overlay instead of silently swallowing.
                if (!Services.WindhawkDetectionService.IsRunningElevated())
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        KaliteOSProgressBar.IsIndeterminate = false;
                        KaliteOSProgressText.Text = "Administrator rights are required to install Windhawk (it installs a system service). Please restart as Administrator - auto-setup will retry on next elevated launch.";
                        KaliteOSProgressBar.Value = 0;
                    });
                    // Keep registry at 0 so next elevated launch retries; auto-hide after delay
                    await Task.Delay(5000);
                    DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
                    return;
                }

                await vm.RunProvisioningAsync();

                // Success check: Windhawk now installed (with CLI)
                bool installed = false;
                try { installed = vm.Installation.IsInstalled && vm.Installation.CliPath != null; } catch { }
                // Also consider legacy installed without CLI as partial success → still mark done to avoid loop
                if (!installed)
                {
                    try { installed = Services.KaliteOSRegistryService.GetIsInstalled() == 1; } catch { }
                    // If ViewModel reports installed at all, treat as success
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
                    // Import may have succeeded partially but verification failed - keep overlay with error
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

        /// <summary>
        /// Verification lock: overlay defaults to Visible in XAML so the first
        /// frame is already covered. Collapse it only when verified; when locked
        /// no page was ever navigated and auto-setup never runs.
        /// Intentionally generic - it never names what is checked.
        /// </summary>
        private void EnforceVerificationLock()
        {
            if (!App.VerificationFailed)
            {
                VerificationOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            VerificationOverlay.Visibility = Visibility.Visible;
            // Park content behind the lock so nothing is operable via keyboard.
            try { NavView.IsEnabled = false; } catch { }
            try { ContentFrame.IsEnabled = false; } catch { }
        }

        private void VerificationExit_Click(object sender, RoutedEventArgs e)
        {
            try { Application.Current.Exit(); } catch { }
            try { Environment.Exit(0); } catch { }
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
