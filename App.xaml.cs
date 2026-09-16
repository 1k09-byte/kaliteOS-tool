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
