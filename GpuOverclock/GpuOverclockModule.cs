using System;
using kaliteConfig.GpuOverclock.Services;

namespace kaliteConfig.GpuOverclock
{
    /// <summary>
    /// Composition root for the overclock module. The app has no DI container —
    /// services are composed explicitly here and exposed through a singleton,
    /// mirroring how ProcessTuning/GamingMode live on App. One instance per
    /// process: NVAPI initializes once, one polling loop, one safety machine.
    /// </summary>
    public sealed class GpuOverclockModule : IDisposable
    {
        private static readonly Lazy<GpuOverclockModule> _lazy = new(() => new GpuOverclockModule());
        public static GpuOverclockModule Instance => _lazy.Value;

        public NvApiGpuController Controller { get; }
        public GpuTelemetryPollingService Telemetry { get; }
        public TdrWatchdogService TdrWatchdog { get; }
        public SafetyRevertService Safety { get; }
        public FanCurveExecutionService FanCurve { get; }
        public ProfileStorageService Profiles { get; }
        public OverclockChangeLogger ChangeLog { get; }
        public StartupTaskService StartupTask { get; }

        private GpuOverclockModule()
        {
            Controller = new NvApiGpuController();
            Telemetry = new GpuTelemetryPollingService(Controller);
            TdrWatchdog = new TdrWatchdogService();
            ChangeLog = new OverclockChangeLogger();
            Safety = new SafetyRevertService(Controller, TdrWatchdog, ChangeLog.Log);
            FanCurve = new FanCurveExecutionService(Controller);
            Profiles = new ProfileStorageService();
            StartupTask = new StartupTaskService();
        }

        public void Dispose()
        {
            Telemetry.Dispose();
            FanCurve.DisposeAsync().GetAwaiter().GetResult();
            Safety.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
