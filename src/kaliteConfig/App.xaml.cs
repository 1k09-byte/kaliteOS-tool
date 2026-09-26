// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using DevWinUI;
using Microsoft.UI.Xaml;
using kaliteConfig.Services;
using kaliteConfig.Native;

namespace kaliteConfig
{
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private Window? _window;
        private static Window? _windowStatic;

        public static bool IsDryRun { get; private set; }
        public static bool IsAdmin { get; private set; }

        /// <summary>
        /// True when this launch came from login autostart (Run key --tray or
        /// the packaged startup task): the window stays hidden and the app
        /// lives in the notification-area tray until opened.
        /// </summary>
        public static bool StartedToTray { get; private set; }

        public IThemeService? ThemeService { get; set; }

        /// <summary>
        /// The app's main window. Needed by file pickers and dialogs in pages
        /// that don't hold a window reference (WinUI 3 unpackaged pattern).
        /// </summary>
        public static Window? MainWindow => _windowStatic;
        
        public ProcessTuningService ProcessTuning { get; } = new ProcessTuningService();
        public ThreadTuningService ThreadTuning { get; } = new ThreadTuningService();
        public CpuSetService CpuSets { get; } = new CpuSetService();
        public NativeSnapshotService NativeSnapshot { get; } = new NativeSnapshotService();

        /// <summary>
        /// Shared Gaming mode instance: the main process list and the Threads
        /// window both go through this, so there is exactly one restore map
        /// and the two UIs can never fight over priorities.
        /// </summary>
        public GamingModeService GamingMode { get; } = new GamingModeService();
        public ForegroundSuspendService ForegroundSuspend { get; }
        public ProfileWatcherService ProfileWatcher { get; }
        public ViewModels.BenchmarkViewModel Benchmark { get; }
        public SnipService Sniper { get; }

        public App()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase))
            {
                IsDryRun = true;
            }

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            InitializeComponent();

            // Last-resort crash log: unhandled UI-thread exceptions land here with
            // a full stack trace so a crash can be diagnosed after the fact.
            this.UnhandledException += (_, e) =>
            {
                // Keep a Games-specific copy as well as the legacy crash log so
                // XAML binding/scan failures can be diagnosed without a debugger.
                Services.GameLibraryService.Log("Unhandled application exception", e.Exception);
                try
                {
                    System.IO.File.AppendAllText("crash.log",
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {e.Exception}\n" +
                        new string('-', 80) + "\n");
                }
                catch { }
            };
            
            ProfileWatcher = new ProfileWatcherService(ProcessTuning, CpuSets, ThreadTuning);
            ForegroundSuspend = new ForegroundSuspendService(ProcessTuning);
            Benchmark = new ViewModels.BenchmarkViewModel();
            Sniper = new SnipService();
            // Fire and forget the profile watcher async load
            _ = InitializeWatcherAsync();
        }

        private async Task InitializeWatcherAsync()
        {
            // Per-process Priority-boost preferences are written through the real
            // tuning service; the store itself stays free of the UI application
            // object so it can be tested headlessly.
            Services.ProcessBoostPreferenceService.Applier =
                (pid, boostEnabled) => ProcessTuning.SetBoostAsync(pid, boostEnabled);

            await ProfileWatcher.LoadProfilesAsync();
            ProfileWatcher.StartWatcher();
            // Keep every Process Control setting applied: rules and boost
            // preferences are re-armed on a fixed cadence, not just once when a
            // process starts.
            // Autostart to tray: the toolkit must be running for rules to
            // apply, so unpackaged installs keep the login Run entry (with
            // --tray) without needing the Settings toggle. Packaged installs
            // need user consent via the StartupTask prompt - those stay on the
            // toggle only.
            _ = EnsureAutostartAsync();
            ProfileWatcher.StartKeeper();
            // Arm Gaming mode for gaming-mode rules whose process is already
            // running (app started mid-game) and watch for exits.
            ProfileWatcher.StartGamingModeWatcher();
#if CONSUMER
            if (UpdateCheckService.TryLaunchInstalledCopyAndExitIfStaleSession())
                return;
#endif
            _ = CheckForUpdateOnStartupAsync();
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
            catch { /* autostart is best-effort; never break startup */ }
        }

        /// <summary>
        /// Consumer startup update dialog: checks GitHub Releases shortly after
        /// the window is up; if a newer full release exists, shows a modal
        /// offering "Update now" (download + silent Inno install + exit) or
        /// "Later". Full flavor: no-op. Never throws - a failed check is a
        /// silent no-op, exactly like the Settings-page banner path.
        /// </summary>
        private async Task CheckForUpdateOnStartupAsync()
        {
#if CONSUMER
            try
            {
                // Small delay so the dialog opens over a rendered window,
                // not during the navigation/layout burst of first launch.
                await Task.Delay(4000);
                var vm = new ViewModels.UpdateViewModel();
                await vm.CheckForUpdateCommand.ExecuteAsync(null);
                if (!vm.IsAvailable || MainWindow is null) return;
                // A failed install is explained once (the Settings banner keeps
                // carrying it) - re-opening a modal for the same version on
                // every launch is the loop users complained about.
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

                // Keep the dialog open while the update downloads/installs.
                // On completion the updater calls Application.Current.Exit(),
                // which tears the process down and closes this dialog.
                // The view model routes internally: hand off to an already
                // installed newer copy, re-run a failed attempt's setup
                // visibly, or download + silent-install the new release.
                dialog.PrimaryButtonClick += (s, args) =>
                {
                    args.Cancel = true; // don't close on click
                    _ = vm.UpdateNowCommand.ExecuteAsync(null);
                };

                await dialog.ShowAsync();

                // Shown → don't explain this same failure again next launch.
                if (vm.LastAttemptFailed)
                    UpdateCheckService.MarkPendingUpdateReported();
            }
            catch { /* update prompting must never break startup */ }
#else
            await Task.CompletedTask;
#endif
        }

        private static System.Threading.Mutex? _singleInstanceMutex;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try 
            {
                _singleInstanceMutex = new System.Threading.Mutex(true, "kaliteConfigAppMutex", out bool isFirstInstance);
                if (!isFirstInstance)
                {
                    // Already running (usually hidden in the tray): ask it to
                    // show its window, then exit instead of starting a duplicate.
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

                _window = new MainWindow();
                _windowStatic = _window;

                // Overclock module teardown on close: stops telemetry, ends the
                // fan-curve loop with a real driver hand-back (spec §5 - closing
                // the app must never leave a forced fan speed), disarms the TDR
                // watchdog. Best-effort; never delays the close.
                _window.Closed += (_, _) =>
                {
                    try { GpuOverclock.GpuOverclockModule.Instance.Dispose(); }
                    catch { }

                    // Gaming mode holds do not survive the process: restore every
                    // demoted process's priority/eco before the app goes away, or
                    // they stay lowered with nothing left to put them back.
                    try { GamingMode.ReleaseAll(); }
                    catch { }
                };

                ThemeService = new ThemeService().Initialize(_window);
                StartedToTray = StartupService.IsTrayLaunch(args.Arguments);
                if (StartedToTray)
                {
                    // Login autostart: live in the tray, no window popup.
                    // The MainWindow constructor already created the tray icon
                    // (with Open/Exit), and the rules engine runs in-proc.
                    // The window was never activated, so it is already hidden -
                    // Hide() just makes that explicit. First tray-Open shows it.
                    _window.AppWindow.Hide();
                }
                else
                {
                    _window.Activate();
                }

                // Reserved CPU sets reapply: the Run key launches the app with
                // --apply-reserved-cpus at login when "Apply at Startup" is on.
                // Re-asserts the saved mask (older Windows builds drop the live
                // kernel reservation across reboots). Best-effort; never breaks
                // app launch.
                if (Environment.GetCommandLineArgs().Contains("--apply-reserved-cpus", StringComparer.OrdinalIgnoreCase))
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var reservedCpus = new ReservedCpuSetsService();
                            if (reservedCpus.GetDesiredMask() is ulong mask && mask != 0)
                                reservedCpus.SetReservedCpuMask(mask);
                        }
                        catch { /* startup reapply must never break app launch */ }
                    });
                }

                // Startup reapply (spec 6): the elevated Task Scheduler task
                // launches the app with --apply-overclock-startup at login.
                // The designated, pre-boot-validated profile is reapplied on a
                // background thread after the driver has settled - through the
                // full safety machine with the shortened headless window.
                if (Environment.GetCommandLineArgs().Contains("--apply-overclock-startup", StringComparer.OrdinalIgnoreCase))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(8000); // let NVAPI/driver settle after login
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
                        catch { /* startup reapply must never break app launch */ }
                    });
                }
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("ExceptionDump.txt", ex.ToString());
                throw;
            }
        }
    }
}
