using System;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using DevWinUI;
using Microsoft.UI.Xaml;
using stellarisKIT.Services;
using stellarisKIT.Native;

namespace stellarisKIT
{
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private Window? _window;
        private static Window? _windowStatic;

        public static bool IsDryRun { get; private set; }
        public static bool IsAdmin { get; private set; }

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
        public ProfileWatcherService ProfileWatcher { get; }

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
                try
                {
                    System.IO.File.AppendAllText("crash.log",
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {e.Exception}\n" +
                        new string('-', 80) + "\n");
                }
                catch { }
            };
            
            ProfileWatcher = new ProfileWatcherService(ProcessTuning, CpuSets, ThreadTuning);
            // Fire and forget the profile watcher async load
            _ = InitializeWatcherAsync();
        }

        private async Task InitializeWatcherAsync()
        {
            await ProfileWatcher.LoadProfilesAsync();
            ProfileWatcher.StartWatcher();
            // Arm Gaming mode for gaming-mode rules whose process is already
            // running (app started mid-game) and watch for exits.
            ProfileWatcher.StartGamingModeWatcher();
            _ = CheckForUpdateOnStartupAsync();
        }

        /// <summary>
        /// Consumer startup update dialog: checks GitHub Releases shortly after
        /// the window is up; if a newer full release exists, shows a modal
        /// offering "Update now" (download + silent Inno install + exit) or
        /// "Later". Full flavor: no-op. Never throws — a failed check is a
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

                var xamlRoot = MainWindow.Content?.XamlRoot;
                if (xamlRoot is null) return;

                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = $"kaliteConfig {vm.LatestVersion} is available",
                    Content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12 },
                    PrimaryButtonText = "Update now",
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
                ((Microsoft.UI.Xaml.Controls.StackPanel)dialog.Content).Children.Add(notes);
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

                // Keep the dialog open while the update downloads/installs;
                // the app exits itself when the installer takes over.
                dialog.PrimaryButtonClick += (s, args) =>
                {
                    args.Cancel = true; // don't close on click
                    _ = vm.UpdateNowCommand.ExecuteAsync(null);
                };

                await dialog.ShowAsync();
            }
            catch { /* update prompting must never break startup */ }
#else
            await Task.CompletedTask;
#endif
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try 
            {
                _window = new MainWindow();
                _windowStatic = _window;
                ThemeService = new ThemeService().Initialize(_window);
                _window.Activate();
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("ExceptionDump.txt", ex.ToString());
                throw;
            }
        }
    }
}
