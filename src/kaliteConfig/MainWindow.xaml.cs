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
using kaliteConfig.Pages;
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
            var appsItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == "AppsPage");
            if (appsItem != null)
                NavView.SelectedItem = appsItem; // fires SelectionChanged -> navigates to Apps (installer)
            else
                ContentFrame.Navigate(typeof(InstallerPage));
            NavView.Loaded += async (_, _) => 
            {
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
            this.Activated += (_, _) => GuardedActivationAsync(TryRunKaliteOSAutoSetupAsync);
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
            ApplyRootBackground();
            if (_materialWatchAttached) return;
            var svc = (Application.Current as App)?.ThemeService;
            if (svc == null) return;
            try
            {
                svc.BackdropChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyRootBackground);
                svc.ThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyRootBackground);
                _materialWatchAttached = true;
                // Service appeared between our last apply and now - re-apply
                // so a persisted non-None material is honored at startup.
                ApplyRootBackground();
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
        private void ApplyRootBackground()
        {
            RootGrid.Background = null;
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
        /// rail item. Margin changes are glide-animated by the pill's
        /// <c>RepositionThemeTransition</c>. Runs on every layout pass but only
        /// assigns when the target actually moved (epsilon guard), so the
        /// assignment itself can't cause a layout loop. No-ops before first layout.
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

            // Pill sits flush against the highlight box's start (left) edge line.
            var box = FindDescendant<Border>(item, "BackgroundPill");
            if (box is null || box.ActualWidth <= 0)
            {
                return;
            }

            var boxPos = box.TransformToVisual(NavShell).TransformPoint(new Point(0, 0));
            double left = boxPos.X;
            if (Math.Abs(NavPill.Margin.Top - top) > 0.5 || Math.Abs(NavPill.Margin.Left - left) > 0.5)
            {
                NavPill.Margin = new Thickness(left, top, 0, 0);
            }

            NavPill.Opacity = 1;
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                UpdateNavPill();
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                switch (tag)
                {
                    case "AppsPage":
                         ContentFrame.Navigate(typeof(InstallerPage));
                         break;
                    case "GraphicsPage":
                         ContentFrame.Navigate(typeof(GraphicsHubPage));
                         break;

                    case "GamingPage":
                         ContentFrame.Navigate(typeof(AffinityPage));
                         break;
                    case "PriorityBoostsPage":
                         ContentFrame.Navigate(typeof(PriorityBoostsPage));
                         break;
                      case "ThreadTunerPage":
                         ContentFrame.Navigate(typeof(ThreadTunerPage));
                         break;
                     case "SnipPage":
                         ContentFrame.Navigate(typeof(SnipPage));
                         break;
                    case "NetworkPage":
                         ContentFrame.Navigate(typeof(NetworkPage));
                         break;case "BenchmarkPage":
                        ContentFrame.Navigate(typeof(BenchmarkPage));
                         break;

                    case "WindowsSettingsPage":
                         ContentFrame.Navigate(typeof(WindowsSettingsHubPage));
                         break;
                    case "BiosManagerPage":
                         ContentFrame.Navigate(typeof(BiosManagerPage));
                         break;
                    case "PowerPlansPage":
                         ContentFrame.Navigate(typeof(PowerPlansPage));
                         break;
                    case "Settings":
                         ContentFrame.Navigate(typeof(SettingsPage));
                         break;
                }

                UpdateNavPill(item);
                PlaySelectPop(item);
            }
        }

        /// <summary>
        /// Plays the selection effect on the rail icon. Driven from code (not a
        /// VSM storyboard) so hovering can neither cancel nor retrigger it.
        /// Crispness-first: transform scale/rotate re-rasterizes the glyph as a
        /// bitmap mid-flight (blur), so the scale pop is kept small and the
        /// effect is carried by an opacity flash (alpha-only, always sharp).
        /// No-ops if the template isn't applied yet.
        /// </summary>
        private static void PlaySelectPop(NavigationViewItem item)
        {
            var presenter = FindDescendant<ContentPresenter>(item, "IconPresenter");
            if (presenter?.RenderTransform is not TransformGroup group
                || group.Children.Count < 1
                || group.Children[0] is not ScaleTransform)
            {
                return;
            }

            var board = new Storyboard();
            var duration = TimeSpan.FromMilliseconds(300);

            DoubleAnimation Track(string property, double from, double to, double amplitude)
            {
                var anim = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = duration,
                    EasingFunction = new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(anim, group.Children[0]);
                Storyboard.SetTargetProperty(anim, property);
                board.Children.Add(anim);
                return anim;
            }

            Track("ScaleX", 0.8, 1, 1.4);
            Track("ScaleY", 0.8, 1, 1.4);

            // Opacity flash: alpha blending never re-rasterizes, stays razor sharp.
            // FillBehavior Stop releases back to the Selected state's breathing
            // loop once the flash finishes (otherwise this HoldEnd value would
            // smother it).
            var flash = new DoubleAnimation
            {
                From = 1,
                To = 0.45,
                Duration = TimeSpan.FromMilliseconds(150),
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop,
            };
            Storyboard.SetTarget(flash, presenter);
            Storyboard.SetTargetProperty(flash, "Opacity");
            board.Children.Add(flash);

            board.Begin();
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
