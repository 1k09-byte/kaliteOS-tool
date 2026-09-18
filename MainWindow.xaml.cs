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

        /// <summary>Real application exit that bypasses minimize-to-tray.</summary>
        public void AllowExitAndClose()
        {
            _allowExit = true;
            try { _tray?.Dispose(); } catch { }
            _tray = null;
            this.Close();
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
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppWindow.Show();
                    var hwnd = WindowNative.GetWindowHandle(this);
                    _ = kaliteConfig.Services.TrayIconService.TrayForeground.BringToFront(hwnd);
                });
            };
            _tray.OnExit += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _allowExit = true;
                    try { _tray?.Dispose(); } catch { }
                    _tray = null;
                    this.Close();
                });
            };
            try
            {
                var trayIcon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                _trayShown = System.IO.File.Exists(trayIcon) && _tray.Show(trayIcon, "kaliteConfig");
            }
            catch { }
            // Persistent active-game-profile indicator (overclock v2 Part B):
            // the user is usually in-game — not looking at the app — when an
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
                // Only swallow the close when the tray is actually up — otherwise
                // a dead tray would make the app unclosable.
                if (!_allowExit && _trayShown)
                {
                    args.Cancel = true;
                    AppWindow.Hide();
                }
            };
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

            WatchMaterialChanges();
            // The ThemeService is initialized in App.OnLaunched after this
            // constructor; retry the subscription once the window activates
            // (and again on first layout) so it always lands.
            this.Activated += (_, _) => WatchMaterialChanges();

            // Default to the Apps (installer/uninstaller) page on launch.
            // Pill position is layout-driven (LayoutUpdated): event-driven updates
            // (SelectionChanged/SizeChanged) can compute against a stale layout
            // pass when maximizing/restoring, stranding the pill on the wrong item.
            NavShell.LayoutUpdated += (_, _) => UpdateNavPill();
            var appsItem = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => (i.Tag as string) == "AppsPage");
            if (appsItem != null)
                NavView.SelectedItem = appsItem; // fires SelectionChanged -> navigates to Apps (installer)
            else
                ContentFrame.Navigate(typeof(UninstallerPage));
            NavView.Loaded += async (_, _) => 
            {
                SuppressSidebarTooltips();
                if (!App.IsAdmin)
                {
                    await ShowElevationDialogAsync();
                }
                await TryRunKaliteOSAutoSetupAsync();
            };
            // Window has no Loaded event (WinUI 3) — also schedule via Activated so auto-setup
            // is not missed if NavView is already loaded before we subscribe.
            this.Activated += async (_, _) => await TryRunKaliteOSAutoSetupAsync();
            this.Activated += (_, _) => { if (!_versionToastShown) { _versionToastShown = true; ShowVersionToast(); } };
            SuppressSidebarTooltips();
        }

        private bool _versionToastShown;
        private bool _materialWatchAttached;

        /// <summary>
        /// Keeps the root background in sync with the material setting:
        /// opaque theme background when Material = None (otherwise the raw
        /// black window surface shows through the transparent nav pane),
        /// transparent when a backdrop is active (backdrops render behind the
        /// XAML content — an opaque root would cover them).
        ///
        /// NOTE: the ThemeService is created in App.OnLaunched AFTER this
        /// window's constructor runs, so an early subscribe attempt would see
        /// null and bail forever. Subscribe lazily: try on every call, and
        /// once the service exists attach the handlers (idempotent).
        /// </summary>
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
                // Service appeared between our last apply and now — re-apply
                // so a persisted non-None material is honored at startup.
                ApplyRootBackground();
            }
            catch { }
        }

        private void ApplyRootBackground()
        {
            var svc = (Application.Current as App)?.ThemeService;
            var backdropType = DevWinUI.BackdropType.None;
            try
            {
                if (svc != null) backdropType = svc.BackdropType;
            }
            catch { }
            bool hasBackdrop = backdropType != DevWinUI.BackdropType.None;
            RootGrid.Background = hasBackdrop
                ? null // let the backdrop show through
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]; // opaque
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
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                switch (tag)
                {
                    case "AppsPage":
                         ContentFrame.Navigate(typeof(InstallerPage));
                         break;
                    case "DriversPage":
                         ContentFrame.Navigate(typeof(GpuDriversPage));
                         break;
                    case "GamingPage":
                         ContentFrame.Navigate(typeof(AffinityPage));
                         break;
                    case "ThreadTunerPage":
                         ContentFrame.Navigate(typeof(ThreadTunerPage));
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
                    case "UninstallerPage":
                         ContentFrame.Navigate(typeof(UninstallerPage));
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
                        KaliteOSProgressText.Text = "Administrator rights are required to install Windhawk (it installs a system service). Please restart as Administrator — auto-setup will retry on next elevated launch.";
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
                        KaliteOSProgressText.Text = "Done — Windhawk installed and KaliteOS mods imported.";
                        KaliteOSPercentText.Text = "100%";
                        KaliteOSPercentText.Visibility = Visibility.Visible;
                    });
                    await Task.Delay(2200);
                    DispatcherQueue.TryEnqueue(() => KaliteOSStartupOverlay.Visibility = Visibility.Collapsed);
                }
                else
                {
                    // Import may have succeeded partially but verification failed — keep overlay with error
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
                    KaliteOSProgressText.Text = $"Auto-setup failed: {ex.Message} — will retry on next launch.";
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

        private async void ShowVersionToast()
        {
#if DEBUG
            VersionText.Text = "Welcome to dev tool";
#else
            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                var version = package.Id.Version;
                VersionText.Text = $"kaliteConfig v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                VersionText.Text = $"kaliteConfig v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "release"}";
            }
#endif

            var board = new Storyboard();
            
            var opAnim = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(400) };
            Storyboard.SetTarget(opAnim, VersionToast);
            Storyboard.SetTargetProperty(opAnim, "Opacity");
            
            var trAnim = new DoubleAnimation { To = 84, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(trAnim, VersionToastTransform);
            Storyboard.SetTargetProperty(trAnim, "Y");

            board.Children.Add(opAnim);
            board.Children.Add(trAnim);
            board.Begin();

            await Task.Delay(8000);

            var boardOut = new Storyboard();
            var opAnimOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(400) };
            Storyboard.SetTarget(opAnimOut, VersionToast);
            Storyboard.SetTargetProperty(opAnimOut, "Opacity");
            
            var trAnimOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(trAnimOut, VersionToastTransform);
            Storyboard.SetTargetProperty(trAnimOut, "Y");

            boardOut.Children.Add(opAnimOut);
            boardOut.Children.Add(trAnimOut);
            boardOut.Begin();
        }
    }
}
