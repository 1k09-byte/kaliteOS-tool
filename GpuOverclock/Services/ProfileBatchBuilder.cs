using System;
using System.Collections.Generic;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Builds the safety-machine batch for "apply profile X" identically for
    /// every apply path (interactive ViewModel apply, headless startup
    /// reapply, unattended game auto-switch). One shared builder means a
    /// manually-validated profile applies byte-identically when a game
    /// triggers it later — profile trustworthiness (Part B's foundation)
    /// never depends on which path applied it.
    ///
    /// V/F curve handling: the profile stores offsets against the base curve
    /// of the GPU it was saved on. Against the LIVE curve they are clamped to
    /// the live per-point ranges; a point-count mismatch or a monotonicity
    /// violation skips ONLY the curve portion (reported via
    /// <paramref name="curveSkippedReason"/>) — the rest of the profile still
    /// applies. The per-point table wins over the flat simple-mode value
    /// when both are present.
    /// </summary>
    public static class ProfileBatchBuilder
    {
        public static List<PendingChange> Build(
            INvidiaGpuController controller,
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
                    // Saved against a different base (or hand-edited JSON):
                    // never submit a non-monotonic curve to the driver.
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

        /// <summary>
        /// Resolves a profile's stored curve settings against the live curve.
        /// Null when the profile doesn't touch the curve OR the stored table
        /// can't be mapped (count mismatch — <paramref name="skipReason"/>
        /// explains, so callers can log it instead of failing silently).
        /// </summary>
        public static List<int>? ResolveCurveOffsets(
            OverclockProfile profile, GpuVoltageFrequencyCurve live, out string? skipReason)
        {
            skipReason = null;
            if (profile.VfCurveOffsets is { Count: > 0 } table)
            {
                if (table.Count != live.Count)
                {
                    skipReason = $"V/F curve skipped: profile stores {table.Count} points " +
                                 $"but this GPU exposes {live.Count} — re-save the profile on this GPU.";
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
