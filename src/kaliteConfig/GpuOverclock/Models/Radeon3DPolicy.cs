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
    public sealed record Radeon3DWrite(Radeon3DSetting Setting, bool Enable, string Reason);

    public sealed record Radeon3DExclusiveGroup
    {
        public static readonly Radeon3DExclusiveGroup None = new();

        public bool AntiLagEnabled { get; init; }
        public bool BoostEnabled { get; init; }
        public bool ChillEnabled { get; init; }

        public bool RestoreAntiLag { get; init; }

        public bool RestoreBoost { get; init; }

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

        public bool HasPendingRestore => RestoreAntiLag || RestoreBoost;
    }

    public sealed record Radeon3DTransition(Radeon3DExclusiveGroup After, IReadOnlyList<Radeon3DWrite> Writes);

    public static class Radeon3DPolicy
    {
        public const string ExclusionExplanation =
            "Radeon Chill cannot run together with Radeon Boost or Radeon Anti-Lag - all three " +
            "drive the same frame-pacing hardware. Turning one on turns the others off, and " +
            "turning Chill off restores whatever was on before. Boost and Anti-Lag can run together.";

        public static IReadOnlyList<Radeon3DSetting> ConflictsWith(Radeon3DSetting setting) => setting switch
        {
            Radeon3DSetting.Chill => new[] { Radeon3DSetting.Boost, Radeon3DSetting.AntiLag },
            Radeon3DSetting.Boost => new[] { Radeon3DSetting.Chill },
            Radeon3DSetting.AntiLag => new[] { Radeon3DSetting.Chill },
            _ => System.Array.Empty<Radeon3DSetting>(),
        };

        public static Radeon3DTransition Request(Radeon3DExclusiveGroup current, Radeon3DSetting setting, bool enable)
        {
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

        public static bool IsExclusive(Radeon3DSetting setting)
            => ConflictsWith(setting).Count > 0;
    }

}
