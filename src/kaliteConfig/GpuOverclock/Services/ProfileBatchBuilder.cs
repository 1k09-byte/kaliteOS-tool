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
using System.Collections.Generic;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public static class ProfileBatchBuilder
    {
        public static List<PendingChange> Build(
            IGpuTuningController controller,
            GpuCapabilities caps,
            OverclockProfile profile,
            Func<string, string> oldDisplay,
            GpuVoltageFrequencyCurve? liveCurve,
            out string? curveSkippedReason)
        {
            curveSkippedReason = null;
            var batch = new List<PendingChange>();

            if (profile.CoreOffsetMHz is { } core && caps.CoreOffsetRangeMHz is not null)
                batch.Add(new PendingChange(OcControlNames.CoreClockOffset,
                    () => controller.SetCoreOffsetMhz(core),
                    oldDisplay(OcControlNames.CoreClockOffset), $"{core:+0;-0;0} MHz"));
            if (profile.MemOffsetMHz is { } mem && caps.MemOffsetRangeMHz is not null)
                batch.Add(new PendingChange(OcControlNames.MemoryClockOffset,
                    () => controller.SetMemoryOffsetMhz(mem),
                    oldDisplay(OcControlNames.MemoryClockOffset), $"{mem:+0;-0;0} MHz"));
            if (profile.PowerLimitPercent is { } power && caps.PowerLimitRangePercent is not null)
                batch.Add(new PendingChange(OcControlNames.PowerLimit,
                    () => controller.SetPowerLimitPercent(power),
                    oldDisplay(OcControlNames.PowerLimit), $"{power:0.#}%"));
            if (profile.TempLimitC is { } temp && caps.TempLimitRangeC is not null)
                batch.Add(new PendingChange(OcControlNames.TemperatureLimit,
                    () => controller.SetTempLimitC(temp),
                    oldDisplay(OcControlNames.TemperatureLimit), $"{temp} °C"));

            if (caps.VfCurveSupported && liveCurve is not null)
            {
                var offsets = ResolveCurveOffsets(profile, liveCurve, out string? skip);
                if (offsets is null)
                {
                    curveSkippedReason = skip;
                }
                else if (!liveCurve.ValidateMonotonic(offsets, out string? monoError))
                {
                    curveSkippedReason = $"V/F curve skipped: {monoError}";
                }
                else
                {
                    var snapshot = offsets.ToArray();
                    batch.Add(new PendingChange(OcControlNames.VoltageFrequencyCurve,
                        () => controller.SetVoltageFrequencyCurveOffsets(snapshot),
                        oldDisplay(OcControlNames.VoltageFrequencyCurve),
                        DescribeCurve(profile, snapshot)));
                }
            }

            return batch;
        }

        public static List<int>? ResolveCurveOffsets(
            OverclockProfile profile, GpuVoltageFrequencyCurve live, out string? skipReason)
        {
            skipReason = null;
            if (profile.VfCurveOffsets is { Count: > 0 } table)
            {
                if (table.Count != live.Count)
                {
                    skipReason = $"V/F curve skipped: profile stores {table.Count} points " +
                                 $"but this GPU exposes {live.Count} - re-save the profile on this GPU.";
                    return null;
                }
                return live.ClampOffsets(table);
            }
            if (profile.GlobalVoltageBoostOffsetMHz is { } flat)
                return live.ExpandFlatOffset(flat);
            return null;
        }

        private static string DescribeCurve(OverclockProfile profile, int[] snapshot)
        {
            if (profile.VfCurveOffsets is { Count: > 0 })
            {
                int min = int.MaxValue, max = int.MinValue;
                foreach (var o in snapshot) { if (o < min) min = o; if (o > max) max = o; }
                return min == max ? $"flat {min:+0;-0;0} MHz ({snapshot.Length} pts)" : $"{min:+0;-0;0}…{max:+0;-0;0} MHz ({snapshot.Length} pts)";
            }
            return $"flat {profile.GlobalVoltageBoostOffsetMHz!.Value:+0;-0;0} MHz ({snapshot.Length} pts)";
        }
    }
}
