using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>Static identity of the detected NVIDIA GPU.</summary>
    public sealed record GpuIdentity
    {
        public required string FullName { get; init; }
        public required string DriverVersion { get; init; }
        public required bool IsNotebook { get; init; }
        public required uint PciDeviceId { get; init; }
    }

    /// <summary>
    /// The module's only door to NVAPI. ViewModels and services never touch
    /// NvAPIWrapper types directly — this isolates wrapper breaking-changes and
    /// makes the write path unit-testable via a fake implementation.
    ///
    /// Threading model: all methods are synchronous and serialized internally
    /// (NVAPI is not thread-safe); callers that must not block the UI thread
    /// (polling loop, apply paths) invoke them via Task.Run.
    /// Expected failures are returned as GpuResult values, never thrown.
    /// </summary>
    public interface INvidiaGpuController
    {
        bool IsInitialized { get; }

        /// <summary>Initializes NVAPI exactly once per process. Idempotent.</summary>
        GpuResult Initialize();

        /// <summary>Picks the primary (first discrete) NVIDIA GPU and reads its identity.</summary>
        GpuResult<GpuIdentity> GetIdentity();

        /// <summary>One telemetry snapshot; null fields mean "not reported by this GPU".</summary>
        GpuResult<GpuTelemetrySnapshot> ReadTelemetry();

        /// <summary>
        /// Per-control ranges from the driver at detection time. Re-query on
        /// GPU re-detect; ranges are not guaranteed stable across driver reloads.
        /// </summary>
        GpuResult<GpuCapabilities> ReadCapabilities();

        /// <summary>Current applied clock deltas in MHz — the revert anchor and readback source.</summary>
        GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets();

        /// <summary>Current power limit (%) and temp limit (°C) — revert anchors.</summary>
        GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits();

        GpuResult SetCoreOffsetMhz(int offsetMhz);
        GpuResult SetMemoryOffsetMhz(int offsetMhz);
        GpuResult SetPowerLimitPercent(double percent);
        GpuResult SetTempLimitC(int tempLimitC);

        /// <summary>Forces a fixed fan percent (writes CoolerPolicy.Manual).</summary>
        GpuResult SetFanStaticPercent(int percent);

        /// <summary>
        /// Explicitly hands fan control back to the driver default — a real
        /// restore call, so a stale forced speed can never persist silently.
        /// </summary>
        GpuResult RestoreFanAuto();
    }
}
