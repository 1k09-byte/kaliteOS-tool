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
    public sealed class GpuOverclockModule : IDisposable
    {
        private static readonly Lazy<GpuOverclockModule> _lazy = new(() => new GpuOverclockModule());
        public static GpuOverclockModule Instance => _lazy.Value;

        public string ControllerVendor { get; }

        public bool IsSimulation => AmdGpuSimulation.IsActive;

        public string SimulationLabel => AmdGpuSimulation.Label;

        public IGpuTuningController Controller { get; }
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
            string? forced = Environment.GetEnvironmentVariable("KALITE_OC_BACKEND")?.Trim().ToLowerInvariant();

            if (AmdGpuSimulation.IsActive)
            {
                var preview = new AmdGpuTuningController();
                preview.Initialize();
                Controller = preview;
                ControllerVendor = "AMD (simulated)";
            }
            else if (forced == "amd")
            {
                var forcedAmd = new AmdGpuTuningController();
                forcedAmd.Initialize();
                Controller = forcedAmd;
                ControllerVendor = "AMD (forced)";
            }
            else
            {
                var nvidia = new NvidiaGpuTuningController();
                if (nvidia.Initialize().IsSuccess)
                {
                    Controller = nvidia;
                    ControllerVendor = "NVIDIA";
                }
                else
                {
                    var amd = new AmdGpuTuningController();
                    amd.Initialize();
                    Controller = amd;
                    ControllerVendor = "AMD";
                }
            }

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
