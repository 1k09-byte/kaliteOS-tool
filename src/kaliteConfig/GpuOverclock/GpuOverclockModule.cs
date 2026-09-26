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
using System;
using kaliteConfig.GpuOverclock.Services;

namespace kaliteConfig.GpuOverclock
{
    /// <summary>
    /// Composition root for the overclock module. The app has no DI container -
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
        public GameProcessWatcherService GameWatcher { get; }
        public GameProfileAutoApplyService GameAutoApply { get; }

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
            GameWatcher = new GameProcessWatcherService();
            GameAutoApply = new GameProfileAutoApplyService(Controller, Safety, Profiles, ChangeLog, FanCurve, GameWatcher);
        }

        public void Dispose()
        {
            try { GameAutoApply.Dispose(); } catch { }
            try { GameWatcher.Dispose(); } catch { }
            Telemetry.Dispose();
            FanCurve.DisposeAsync().GetAwaiter().GetResult();
            Safety.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
