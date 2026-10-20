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
