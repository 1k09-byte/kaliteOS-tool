using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using stellarisKIT.Pages;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using WinRT.Interop;

namespace stellarisKIT
{
    public sealed partial class MainWindow : Window
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private stellarisKIT.Services.TrayIconService? _tray;
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
                stellarisKIT.Native.DwmApi.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Cosmetic only: a square window is still fully usable.
            }

            ExtendsContentIntoTitleBar = true;
            AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1440, Height = 900 });
            AppWindow.Title = "kaliteConfig";
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "kaliteConfig.ico");
                if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { } // icon is cosmetic; never block startup

            // Close button minimizes to the system tray instead of exiting.
            // Exit only via the tray menu (or Settings, which calls AllowExit+Close).
            _tray = new stellarisKIT.Services.TrayIconService();
            _tray.OnOpen += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppWindow.Show();
                    var hwnd = WindowNative.GetWindowHandle(this);
                    _ = stellarisKIT.Services.TrayIconService.TrayForeground.BringToFront(hwnd);
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
            };
            SuppressSidebarTooltips();
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
