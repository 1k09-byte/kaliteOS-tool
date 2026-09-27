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
    /// process: the vendor API initializes once, one polling loop, one safety
    /// machine.
    ///
    /// Controller is typed as the vendor-neutral interface, not the NVAPI
    /// implementation. That is deliberate: exposing the concrete type let the
    /// ViewModel call members that exist only on NVIDIA's API, which is exactly
    /// the coupling a second vendor backend must not inherit.
    /// </summary>
    public sealed class GpuOverclockModule : IDisposable
    {
        private static readonly Lazy<GpuOverclockModule> _lazy = new(() => new GpuOverclockModule());
        public static GpuOverclockModule Instance => _lazy.Value;

        /// <summary>Which backend won. Drives what the page says it found.</summary>
        public string ControllerVendor { get; }

        /// <summary>
        /// True when the page is showing a fabricated AMD card
        /// (<see cref="AmdGpuSimulation"/>). The UI says so on the page; nothing
        /// applied here reaches a driver.
        /// </summary>
        public bool IsSimulation => AmdGpuSimulation.IsActive;

        /// <summary>Which simulated card is on screen, for the banner.</summary>
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
            // Prefer NVIDIA when one is present: its backend is the complete one.
            // Fall back to AMD, whose backend reports what the driver allows. A
            // machine with neither still gets a controller, so the page can show
            // a real reason instead of throwing.
            //
            // KALITE_OC_BACKEND=amd|nvidia forces a backend. Without it the AMD
            // path is unreachable on a machine that also has an NVIDIA card, which
            // makes it impossible to see working before shipping to someone who
            // has only an AMD GPU.
            string? forced = Environment.GetEnvironmentVariable("KALITE_OC_BACKEND")?.Trim().ToLowerInvariant();

            // KALITE_OC_SIMULATE=amd[ -prenavi4 | -igpu] shows a fabricated Radeon
            // instead of the real adapter, so the AMD interface can be looked at
            // on a machine that has no discrete Radeon. It wins over backend
            // selection on purpose: a preview of the AMD page is the whole point,
            // and on a mixed machine the NVIDIA backend would otherwise win and
            // hide it. No ADLX call is made in this mode.
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
