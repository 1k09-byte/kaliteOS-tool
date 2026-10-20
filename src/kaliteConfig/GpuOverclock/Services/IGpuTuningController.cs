using System.Collections.Generic;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed record GpuIdentity
    {
        public required string FullName { get; init; }
        public required string DriverVersion { get; init; }
        public required bool IsNotebook { get; init; }
        public required uint PciDeviceId { get; init; }
    }

    public interface IGpuTuningController
    {
        bool IsInitialized { get; }

        GpuResult Initialize();

        GpuResult<GpuIdentity> GetIdentity();

        void InvalidateGpu();

        GpuResult<GpuTelemetrySnapshot> ReadTelemetry();

        GpuResult<GpuCapabilities> ReadCapabilities();

        GpuResult<(int CoreOffsetMHz, int MemOffsetMHz)> ReadCurrentOffsets();

        GpuResult<(double PowerLimitPercent, int? TempLimitC)> ReadCurrentLimits();

        GpuResult SetCoreOffsetMhz(int offsetMhz);
        GpuResult SetMemoryOffsetMhz(int offsetMhz);
        GpuResult SetPowerLimitPercent(double percent);
        GpuResult SetTempLimitC(int tempLimitC);

        GpuResult SetFanStaticPercent(int percent);

        GpuResult RestoreFanAuto();

        GpuResult<bool> IsZeroRpmEnabled()
            => GpuResult<bool>.Fail(
                OverclockErrorKind.ControlUnsupported,
                "Zero RPM is not available on this GPU or driver.");

        GpuResult SetZeroRpmEnabled(bool enabled)
            => GpuResult.Fail(
                OverclockErrorKind.ControlUnsupported,
                "Zero RPM is not available on this GPU or driver.");

        GpuResult<GpuVoltageFrequencyCurve> ReadVoltageFrequencyCurve();

        GpuResult SetVoltageFrequencyCurveOffsets(IReadOnlyList<int> offsetsMhz);

        GpuResult<int[]> ReadVfCurveOffsets();

        GpuResult<uint> ReadVoltageBoostPercent();

        GpuResult SetVoltageBoostPercent(uint percent);
    }
}
