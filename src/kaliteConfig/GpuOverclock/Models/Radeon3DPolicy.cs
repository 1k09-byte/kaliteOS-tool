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

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>One intended ADLX write, in the order it must be issued.</summary>
    public sealed record Radeon3DWrite(Radeon3DSetting Setting, bool Enable, string Reason);

    /// <summary>
    /// What the driver currently says about the mutually exclusive group, plus
    /// what this page owes the driver when Chill is switched off again.
    ///
    /// This is a value, not a cache: it is rebuilt from a fresh read-back after
    /// every write, so what the UI shows is what the driver last confirmed
    /// rather than what the user clicked.
    /// </summary>
    public sealed record Radeon3DExclusiveGroup
    {
        public static readonly Radeon3DExclusiveGroup None = new();

        public bool AntiLagEnabled { get; init; }
        public bool BoostEnabled { get; init; }
        public bool ChillEnabled { get; init; }

        /// <summary>Anti-Lag was on when this page had to switch it off for Chill.</summary>
        public bool RestoreAntiLag { get; init; }

        /// <summary>Boost was on when this page had to switch it off for Chill.</summary>
        public bool RestoreBoost { get; init; }

        /// <summary>Builds a group from a read-back of the three interfaces.</summary>
        public static Radeon3DExclusiveGroup FromReadback(bool antiLag, bool boost, bool chill)
            => new() { AntiLagEnabled = antiLag, BoostEnabled = boost, ChillEnabled = chill };

        public bool IsEnabled(Radeon3DSetting setting) => setting switch
        {
            Radeon3DSetting.AntiLag => AntiLagEnabled,
            Radeon3DSetting.Boost => BoostEnabled,
            Radeon3DSetting.Chill => ChillEnabled,
            _ => false,
        };

        public Radeon3DExclusiveGroup With(Radeon3DSetting setting, bool enabled) => setting switch
        {
            // Switching a feature ON clears any pending restore: it is on, so
            // there is nothing left to put back. Switching it off leaves the
            // flag alone, because only Request knows whether this page turned it
            // off for Chill or the user did.
            Radeon3DSetting.AntiLag => this with
            {
                AntiLagEnabled = enabled,
                RestoreAntiLag = enabled ? false : RestoreAntiLag,
            },
            Radeon3DSetting.Boost => this with
            {
                BoostEnabled = enabled,
                RestoreBoost = enabled ? false : RestoreBoost,
            },
            Radeon3DSetting.Chill => this with { ChillEnabled = enabled },
            _ => this,
        };

        /// <summary>What still needs switching on if Chill is turned off right now.</summary>
        public bool HasPendingRestore => RestoreAntiLag || RestoreBoost;
    }

    /// <summary>
    /// The writes a request needs, in order, and the group those writes imply.
    /// </summary>
    public sealed record Radeon3DTransition(Radeon3DExclusiveGroup After, IReadOnlyList<Radeon3DWrite> Writes);

    /// <summary>
    /// The mutual-exclusivity rules of the driver's 3D settings, as pure data.
    ///
    /// The one hard rule AMD documents is that Chill cannot run at the same time
    /// as Boost or Anti-Lag: all three compete for the same frame-pacing
    /// mechanism. Boost and Anti-Lag are NOT mutually exclusive with each other -
    /// Adrenalin lets you run both, and so does the driver - so this is a three
    /// member group with one edge, not a radio group of three. The page presents
    /// it as one card and says so, rather than pretending otherwise.
    ///
    /// The second half of the rule is the part that is easy to get wrong:
    /// switching Chill on has to switch the others OFF, and switching it back
    /// off has to switch them back ON to what they were. That is why the group
    /// carries Restore* flags rather than just three booleans.
    /// </summary>
    public static class Radeon3DPolicy
    {
        /// <summary>The plain-language explanation shown above the group.</summary>
        public const string ExclusionExplanation =
            "Radeon Chill cannot run together with Radeon Boost or Radeon Anti-Lag - all three " +
            "drive the same frame-pacing hardware. Turning one on turns the others off, and " +
            "turning Chill off restores whatever was on before. Boost and Anti-Lag can run together.";

        /// <summary>What the driver will not allow to be on at the same time as <paramref name="setting"/>.</summary>
        public static IReadOnlyList<Radeon3DSetting> ConflictsWith(Radeon3DSetting setting) => setting switch
        {
            Radeon3DSetting.Chill => new[] { Radeon3DSetting.Boost, Radeon3DSetting.AntiLag },
            Radeon3DSetting.Boost => new[] { Radeon3DSetting.Chill },
            Radeon3DSetting.AntiLag => new[] { Radeon3DSetting.Chill },
            _ => System.Array.Empty<Radeon3DSetting>(),
        };

        /// <summary>
        /// Plans the writes for one toggle. Pure: it reads the current read-back
        /// and returns the ordered writes plus the group they imply, and touches
        /// nothing. The caller issues the writes and then rebuilds the group from
        /// a fresh read-back of all three interfaces.
        /// </summary>
        public static Radeon3DTransition Request(Radeon3DExclusiveGroup current, Radeon3DSetting setting, bool enable)
        {
            // Already in the requested state: nothing to do. This also makes the
            // request idempotent, which matters because a UI toggle can raise
            // its changed event when the page re-asserts the driver's value -
            // that must not turn into a second write.
            if (current.IsEnabled(setting) == enable)
                return new Radeon3DTransition(current, System.Array.Empty<Radeon3DWrite>());

            var writes = new List<Radeon3DWrite>();

            if (setting == Radeon3DSetting.Chill)
            {
                writes.AddRange(
                    enable ? EnableChillWrites(current) : DisableChillWrites(current));
            }
            else
            {
                writes.AddRange(ToggleWrites(current, setting, enable));
            }

            if (writes.Count == 0)
                return new Radeon3DTransition(current, writes);

            var after = current;
            foreach (Radeon3DWrite write in writes)
                after = after.With(write.Setting, write.Enable);

            // Anything switched off to make room for Chill has to be remembered,
            // because turning Chill off must put it back. The flag is "what it
            // was before we touched it" - With() deliberately cannot infer that.
            if (setting == Radeon3DSetting.Chill && enable)
            {
                if (!after.BoostEnabled && current.BoostEnabled) after = after with { RestoreBoost = true };
                if (!after.AntiLagEnabled && current.AntiLagEnabled) after = after with { RestoreAntiLag = true };
            }

            return new Radeon3DTransition(after, writes);
        }

        private static IEnumerable<Radeon3DWrite> EnableChillWrites(Radeon3DExclusiveGroup current)
        {
            var writes = new List<Radeon3DWrite>();

            // Off first: the driver will not accept Chill while a competitor is
            // still on, so the order is not cosmetic.
            if (current.BoostEnabled)
                writes.Add(new Radeon3DWrite(Radeon3DSetting.Boost, false,
                    "Radeon Boost was switched off because Chill cannot run alongside it."));
            if (current.AntiLagEnabled)
                writes.Add(new Radeon3DWrite(Radeon3DSetting.AntiLag, false,
                    "Radeon Anti-Lag was switched off because Chill cannot run alongside it."));

            writes.Add(new Radeon3DWrite(Radeon3DSetting.Chill, true,
                "Radeon Chill switched on."));

            return writes;
        }

        private static IEnumerable<Radeon3DWrite> DisableChillWrites(Radeon3DExclusiveGroup current)
        {
            var writes = new List<Radeon3DWrite>();

            if (current.ChillEnabled)
                writes.Add(new Radeon3DWrite(Radeon3DSetting.Chill, false,
                    "Radeon Chill switched off."));

            // Then put back whatever was on before this page had to take it
            // off. Only what WE disabled is restored - a feature the user turns
            // off by hand stays off.
            if (current.RestoreBoost)
                writes.Add(new Radeon3DWrite(Radeon3DSetting.Boost, true,
                    "Radeon Boost restored to how it was before Chill."));
            if (current.RestoreAntiLag)
                writes.Add(new Radeon3DWrite(Radeon3DSetting.AntiLag, true,
                    "Radeon Anti-Lag restored to how it was before Chill."));

            return writes;
        }

        private static IEnumerable<Radeon3DWrite> ToggleWrites(Radeon3DExclusiveGroup current, Radeon3DSetting setting, bool enable)
        {
            var writes = new List<Radeon3DWrite>();

            if (enable)
            {
                if (current.ChillEnabled)
                    writes.Add(new Radeon3DWrite(Radeon3DSetting.Chill, false,
                        "Radeon Chill was switched off because it cannot run alongside " +
                        (setting == Radeon3DSetting.Boost ? "Radeon Boost." : "Radeon Anti-Lag.")));

                writes.Add(new Radeon3DWrite(setting, true, "Switched on."));
            }
            else
            {
                writes.Add(new Radeon3DWrite(setting, false, "Switched off."));
            }

            return writes;
        }

        /// <summary>
        /// Why a feature is in the group but why it is NOT written: used by the
        /// page's explanation, and by tests that assert the rule is one edge and
        /// not a full radio group.
        /// </summary>
        public static bool IsExclusive(Radeon3DSetting setting)
            => ConflictsWith(setting).Count > 0;
    }

    /// <summary>
    /// FPS / sharpness validation against the driver's own ADLX_IntRange.
    ///
    /// Every range on this page comes from GetFPSRange, GetSharpnessRange or
    /// GetResolutionRange - the driver's reported min, max and step. Nothing here
    /// invents a default, and a range the driver reports as unusable is refused
    /// rather than replaced with a made-up one.
    /// </summary>
    public static class RadeonRange
    {
        /// <summary>
        /// True when the driver's range can be used as a slider/number box: a
        /// step of zero (or negative) would make the arithmetic below divide by
        /// zero, and an inverted range is a driver we do not understand.
        /// </summary>
        public static bool IsUsable(int minValue, int maxValue, int step)
            => step > 0 && maxValue >= minValue;

        /// <summary>
        /// Snaps a value onto the driver's step grid, anchored at the minimum.
        ///
        /// The result is never below <paramref name="minValue"/>. Rounding a
        /// value that is under the minimum left it on a grid point underneath
        /// the range - snapping 1 against a 30..240 step-6 range gave 6, which
        /// the driver would refuse and the user would never have asked for.
        /// </summary>
        public static int SnapToStep(int value, int minValue, int step)
        {
            if (step <= 0) return value;
            int steps = (value - minValue + (step / 2)) / step; // rounds half up
            int snapped = minValue + (steps * step);
            return snapped < minValue ? minValue : snapped;
        }

        /// <summary>Clamps into the driver's range, then onto its step grid.</summary>
        public static int Clamp(int value, int minValue, int maxValue, int step)
        {
            int clamped = value < minValue ? minValue : value > maxValue ? maxValue : value;
            int snapped = SnapToStep(clamped, minValue, step);
            return snapped > maxValue ? maxValue : snapped;
        }

        /// <summary>
        /// Normalizes a (min, max) FPS pair against the driver range: each value is
        /// clamped and snapped independently, then a min above its max is treated
        /// as a swap - which is almost always a user who typed them the wrong way
        /// round, and swapping is what they meant.
        ///
        /// <paramref name="note"/> explains any adjustment, or is null when the
        /// pair was already valid. Returns false only when the range itself is
        /// unusable, in which case the caller must not write anything.
        /// </summary>
        public static bool TryNormalizeFpsPair(
            int minFps,
            int maxFps,
            int rangeMin,
            int rangeMax,
            int rangeStep,
            out int normalizedMin,
            out int normalizedMax,
            out string? note)
        {
            normalizedMin = minFps;
            normalizedMax = maxFps;
            note = null;

            if (!IsUsable(rangeMin, rangeMax, rangeStep))
            {
                note = "The AMD driver reported an unusable frame-rate range " +
                       $"({rangeMin}..{rangeMax} step {rangeStep}); nothing was written.";
                return false;
            }

            int clampedMin = Clamp(minFps, rangeMin, rangeMax, rangeStep);
            int clampedMax = Clamp(maxFps, rangeMin, rangeMax, rangeStep);

            bool adjusted = clampedMin != minFps || clampedMax != maxFps;
            if (clampedMin > clampedMax)
            {
                (clampedMin, clampedMax) = (clampedMax, clampedMin);
                adjusted = true;
                note = $"Minimum {minFps} was above maximum {maxFps}, so the two were swapped.";
            }
            else if (adjusted)
            {
                note = $"Adjusted to the driver's supported range {rangeMin}..{rangeMax} " +
                       $"(step {rangeStep}).";
            }

            normalizedMin = clampedMin;
            normalizedMax = clampedMax;
            return true;
        }
    }
}
