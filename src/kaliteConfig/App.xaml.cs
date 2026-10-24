using System;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using kaliteConfig.Native;
using kaliteConfig.Services;
using kaliteConfig.Services.DependencyInjection;

namespace kaliteConfig
{
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private Window? _window;
        private static Window? _windowStatic;

        public static bool IsDryRun { get; private set; }

        public static bool IsDev { get; private set; }
        public static bool IsAdmin { get; private set; }

        public static bool StartedToTray { get; private set; }

        public IThemeService? ThemeService { get; set; }

        private static void SetAppBrush(string key, Windows.UI.Color color)
        {
            try { Application.Current.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color); }
            catch { }
        }

        private static void ApplyNavyAppAccent()
        {
            try
            {
                var res = Application.Current.Resources;
                var navy = Windows.UI.Color.FromArgb(255, 25, 35, 90);
                var navyHover = Windows.UI.Color.FromArgb(255, 48, 55, 115);
                var navyPressed = Windows.UI.Color.FromArgb(255, 15, 21, 54);
                var navyDisabled = Windows.UI.Color.FromArgb(255, 64, 66, 84);
                var navyText = Windows.UI.Color.FromArgb(255, 140, 145, 172);
                var navyTextHover = Windows.UI.Color.FromArgb(255, 197, 200, 214);
                var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                res["SystemAccentColor"] = navy;
                res["SystemAccentColorLight1"] = Windows.UI.Color.FromArgb(255, 82, 90, 131);
                res["SystemAccentColorLight2"] = navyText;
                res["SystemAccentColorLight3"] = navyTextHover;
                res["SystemAccentColorDark1"] = Windows.UI.Color.FromArgb(255, 20, 28, 72);
                res["SystemAccentColorDark2"] = Windows.UI.Color.FromArgb(255, 15, 21, 54);
                res["SystemAccentColorDark3"] = Windows.UI.Color.FromArgb(255, 10, 14, 36);
                SetAppBrush("AccentFillColorDefaultBrush", navy);
                SetAppBrush("AccentFillColorSecondaryBrush", navyHover);
                SetAppBrush("AccentFillColorTertiaryBrush", navyPressed);
                SetAppBrush("AccentTextFillColorPrimaryBrush", navyText);
                SetAppBrush("AccentTextFillColorSecondaryBrush", navyText);
                SetAppBrush("AccentTextFillColorTertiaryBrush", navyTextHover);

                foreach (var key in new[]
                {
                    "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
                    "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver",
                    "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
                    "SliderThumbBackground", "SliderThumbBackgroundPointerOver",
                    "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
                    "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
                    "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedFillPointerOver",
                    "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver",
                    "ProgressBarForeground", "ProgressBarIndeterminateForegroundThemeBrush",
                    "ProgressRingForegroundThemeBrush", "InfoBadgeBackground",
                }) SetAppBrush(key, navy);

                foreach (var key in new[]
                {
                    "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOnPressed",
                    "SliderTrackValueFillPressed", "SliderThumbBackgroundPressed",
                    "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundFillIndeterminatePressed",
                    "RadioButtonOuterEllipseCheckedFillPressed", "RadioButtonOuterEllipseCheckedStrokePressed",
                    "HyperlinkButtonForegroundPressed",
                }) SetAppBrush(key, navyPressed);

                foreach (var key in new[]
                {
                    "ToggleSwitchFillOnDisabled", "ToggleSwitchStrokeOnDisabled",
                    "SliderTrackValueFillDisabled", "SliderThumbBackgroundDisabled",
                    "CheckBoxCheckBackgroundFillCheckedDisabled", "CheckBoxCheckBackgroundFillIndeterminateDisabled",
                    "RadioButtonOuterEllipseCheckedFillDisabled", "RadioButtonOuterEllipseCheckedStrokeDisabled",
                }) SetAppBrush(key, navyDisabled);

                foreach (var key in new[]
                {
                    "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
                    "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
                    "CheckBoxCheckGlyphForegroundCheckedPressed", "CheckBoxCheckGlyphForegroundIndeterminate",
                    "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
                    "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
                    "InfoBadgeForeground",
                }) SetAppBrush(key, white);

                foreach (var key in new[]
                {
                    "HyperlinkButtonForeground", "HyperlinkButtonForegroundPointerOver",
                }) SetAppBrush(key, navyText);

                SetAppBrush("SliderOuterThumbBackground", white);
                SetAppBrush("SliderThumbBorderBrush", navyPressed);
            }
            catch { }
        }

        private async Task ApplyDefaultAppearanceAsync()
        {
            try
            {
                var theme = ThemeService;
                if (theme is null) return;

                if (!BackdropPreference.MaterialChosen)
                {
                    await theme.SetBackdropTypeAsync(DevWinUI.BackdropType.None);
                }
            }
            catch
            {
            }
        }

        public static Window? MainWindow => _windowStatic;
        
        public static IServiceProvider Services { get; private set; } = null!;

        public ProcessTuningService ProcessTuning => Services.GetRequiredService<ProcessTuningService>();
        public ThreadTuningService ThreadTuning => Services.GetRequiredService<ThreadTuningService>();
        public CpuSetService CpuSets => Services.GetRequiredService<CpuSetService>();
        public NativeSnapshotService NativeSnapshot { get; } = new NativeSnapshotService();

        public GamingModeService LegacyGamingMode => Services.GetRequiredService<GamingModeService>();
        public Gaming.GameMode.GameModeEngine GameMode => Services.GetRequiredService<Gaming.GameMode.GameModeEngine>();
        public ForegroundSuspendService ForegroundSuspend => Services.GetRequiredService<ForegroundSuspendService>();
        public ProfileWatcherService ProfileWatcher => Services.GetRequiredService<ProfileWatcherService>();
        public SnipService Sniper => Services.GetRequiredService<SnipService>();

        public App()
        {
            Services = new ServiceCollection().AddKaliteServices();
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
            {
                IsDryRun = true;
            }
            if (args.Contains("--dev", StringComparer.OrdinalIgnoreCase))
            {
                IsDev = true;
            }

            if (Gaming.GameMode.GameModeEngine.TryRunGuard(args))
            {
                Environment.Exit(0);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { GameMode.ResumeEverything(); } catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                try { GameMode.ResumeEverything(); } catch { }
            };

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            InitializeComponent();
            this.UnhandledException += (_, e) =>
            {
                try
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "kaliteConfig", "logs");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {e.Exception}\n" +
                        new string('-', 80) + "\n");
                }
                catch {  }
            };

            _ = InitializeWatcherAsync();
        }

        private async Task InitializeWatcherAsync()
        {
            try { _ = Services.GetRequiredService<SnipService>(); }
            catch { }
            global::kaliteConfig.Services.ProcessBoostPreferenceService.Applier =
                (pid, boostEnabled) => ProcessTuning.SetBoostAsync(pid, boostEnabled);

            await ProfileWatcher.LoadProfilesAsync();
            ProfileWatcher.StartWatcher();
            _ = EnsureAutostartAsync();
            ProfileWatcher.StartKeeper();
            ProfileWatcher.StartGamingModeWatcher();
            if (!IsDev && UpdateCheckService.TryLaunchInstalledCopyAndExitIfStaleSession())
                return;
            if (!IsDev) _ = CheckForUpdateOnStartupAsync();
        }

        private static async Task EnsureAutostartAsync()
        {
            try
            {
                if (StartupService.IsPackaged) return;
                var startup = new StartupService();
                if (!await startup.IsEnabledAsync())
                    await startup.SetEnabledAsync(true);
            }
            catch {  }
        }

        private async Task CheckForUpdateOnStartupAsync()
        {
            try
            {
                await Task.Delay(4000);
                var vm = new ViewModels.UpdateViewModel();
                await vm.CheckForUpdateCommand.ExecuteAsync(null);
                if (!vm.IsAvailable || MainWindow is null) return;
                if (vm.SuppressStartupOffer) return;

                var xamlRoot = MainWindow.Content?.XamlRoot;
                if (xamlRoot is null) return;

                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = $"kaliteConfig {vm.LatestVersion} is available",
                    Content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12 },
                    PrimaryButtonText = vm.PrimaryUpdateActionText,
                    CloseButtonText = "Later",
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                };
                var notes = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(vm.Notes) ? "No release notes provided." : vm.Notes,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    MaxWidth = 420,
                };
                var progress = new Microsoft.UI.Xaml.Controls.ProgressBar
                {
                    Minimum = 0, Maximum = 100, Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                };
                var status = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    FontSize = 12,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                };
                var scrollViewer = new Microsoft.UI.Xaml.Controls.ScrollViewer
                {
                    Content = notes,
                    MaxHeight = 250,
                    Padding = new Microsoft.UI.Xaml.Thickness(0, 0, 12, 0)
                };
                ((Microsoft.UI.Xaml.Controls.StackPanel)dialog.Content).Children.Add(scrollViewer);
                ((Microsoft.UI.Xaml.Controls.StackPanel)dialog.Content).Children.Add(progress);
                ((Microsoft.UI.Xaml.Controls.StackPanel)dialog.Content).Children.Add(status);

                void OnProgress(double? p)
                {
                    progress.Value = p ?? 0;
                    progress.Visibility = p is null
                        ? Microsoft.UI.Xaml.Visibility.Collapsed
                        : Microsoft.UI.Xaml.Visibility.Visible;
                }

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.DownloadPercent)) OnProgress(vm.DownloadPercent);
                    if (e.PropertyName == nameof(vm.StatusText))
                    {
                        status.Text = vm.StatusText;
                        status.Visibility = string.IsNullOrEmpty(vm.StatusText)
                            ? Microsoft.UI.Xaml.Visibility.Collapsed
                            : Microsoft.UI.Xaml.Visibility.Visible;
                    }
                };

                dialog.PrimaryButtonClick += (s, args) =>
                {
                    args.Cancel = true;
                    _ = vm.UpdateNowCommand.ExecuteAsync(null);
                };

                using var centerLock = kaliteConfig.Services.DialogCenterLock.Attach(dialog);

                await dialog.ShowAsync();

                if (vm.LastAttemptFailed)
                    UpdateCheckService.MarkPendingUpdateReported();
            }
            catch {  }
        }

        private static System.Threading.Mutex? _singleInstanceMutex;

        internal static void ShutdownTrace(string step)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "kaliteConfig", "logs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "shutdown.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {step}{Environment.NewLine}");
            }
            catch {  }
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
            {
                currentProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.Idle;
                currentProcess.PriorityBoostEnabled = false;
            }
            _singleInstanceMutex = new System.Threading.Mutex(true, "kaliteConfigAppMutex", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        try
                        {
                            using var ev = System.Threading.EventWaitHandle.OpenExisting(kaliteConfig.MainWindow.ShowWindowEventName);
                            ev.Set();
                            break;
                        }
                        catch (System.Threading.WaitHandleCannotBeOpenedException)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                }
                catch { }
                Environment.Exit(0);
                return;
            }

            try
            {
                FontPreference.Load();
                FontPreference.Apply();
            }
            catch {  }

            _window = new MainWindow();
            _windowStatic = _window;

            _window.Closed += (_, _) =>
            {
                ShutdownTrace("closed.begin");
                if (_window is MainWindow closingWindow) { try { closingWindow.ShutdownTray(); } catch { } }
                ShutdownTrace("closed.tray-gone");
                try { GpuOverclock.GpuOverclockModule.Instance.Dispose(); }
                catch { }
                ShutdownTrace("closed.gpu-done");

                try { LegacyGamingMode.ReleaseAll(); }
                catch { }
                try { GameMode.Dispose(); }
                catch { }
                try
                {
                    var live = Services?.GetService(typeof(Services.LiveWallpaper.ILiveWallpaperEngine)) as Services.LiveWallpaper.ILiveWallpaperEngine;
                    live?.StopAndRestore();
                }
                catch { }
                ShutdownTrace("closed.end");
            };

            ThemeService = new ThemeService()
                .ConfigureElementTheme(ElementTheme.Dark)
                .ConfigureBackdrop(DevWinUI.BackdropType.None, true)
                .Initialize(_window);
            ApplyNavyAppAccent();
            try { GameMode.Start(); }
            catch { }

            StartedToTray = StartupService.IsTrayLaunch(args.Arguments);
            if (StartedToTray)
            {
                _window.AppWindow.Hide();
            }
            else
            {
                _window.Activate();
                _ = ApplyDefaultAppearanceAsync();
            }

            bool wantsReservedCpus = Environment.GetCommandLineArgs()
                .Contains("--apply-reserved-cpus", StringComparer.OrdinalIgnoreCase);
            if (!wantsReservedCpus)
            {
                try { wantsReservedCpus = new ReservedCpuSetsService().GetApplyAtStartup(); }
                catch { }
            }

            if (wantsReservedCpus)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var reservedCpus = new ReservedCpuSetsService();
                        if (reservedCpus.GetDesiredMask() is ulong mask && mask != 0)
                            reservedCpus.SetReservedCpuMask(mask);
                    }
                    catch {  }
                });
            }

            if (Environment.GetCommandLineArgs().Contains("--apply-overclock-startup", StringComparer.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(8000);
                        var module = GpuOverclock.GpuOverclockModule.Instance;
                        var startup = new GpuOverclock.Services.StartupApplyService(
                            module.Controller, module.Safety, module.Profiles, module.ChangeLog,
                            msg => module.ChangeLog.Log(new GpuOverclock.Models.AppliedChangeLogEntry
                            {
                                Timestamp = DateTime.Now,
                                ControlName = "Startup reapply",
                                OldValue = "-",
                                NewValue = msg,
                                Source = GpuOverclock.Models.OverclockChangeSource.StartupApply,
                                Result = GpuOverclock.Models.OverclockChangeResult.Success,
                            }));
                        await startup.ApplyDefaultProfileAtStartupAsync();
                    }
                    catch {  }
                });
            }
        }
    }
}
