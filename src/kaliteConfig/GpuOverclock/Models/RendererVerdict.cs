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
using System.Linq;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>Which GPU Windows has been told to render a given app on.</summary>
    public enum RendererChoice
    {
        /// <summary>No per-app preference: Windows picks, which is the discrete card for games.</summary>
        Unset,

        /// <summary>"Power saving" - the integrated GPU.</summary>
        PowerSaving,

        /// <summary>"High performance" - the discrete GPU.</summary>
        HighPerformance,
    }

    /// <summary>
    /// Reads Windows' own per-app Graphics preference
    /// (Settings &gt; System &gt; Display &gt; Graphics), as stored under
    /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences.
    ///
    /// The value is a bag of semicolon-separated settings, e.g.
    /// <c>GpuPreference=2;AutoHDREnable=1;</c>, so it is parsed key by key
    /// rather than by position. GpuPreference=1 is "power saving" (the iGPU) and
    /// 2 is "high performance" (the dGPU); anything else is treated as unset
    /// rather than guessed at.
    /// </summary>
    public static class GpuPreferenceParser
    {
        public static RendererChoice? ParsePreference(string? valueData)
        {
            if (string.IsNullOrWhiteSpace(valueData)) return null;

            foreach (string part in valueData.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part.AsSpan(0, eq).Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase))
                    continue;

                string raw = part[(eq + 1)..].Trim();
                return raw switch
                {
                    "1" => RendererChoice.PowerSaving,
                    "2" => RendererChoice.HighPerformance,
                    _ => null,
                };
            }

            return null;
        }
    }

    /// <summary>What the Radeon tab should say about where an app actually renders.</summary>
    public sealed record RendererVerdict(string Title, string Detail, bool SettingsApply)
    {
        public bool NeedsWarning => !SettingsApply;
    }

    /// <summary>
    /// Turns "which GPU is this app on" into a sentence the user can act on.
    ///
    /// This exists because the Radeon 3D settings are genuinely useless on an app
    /// that is rendering somewhere else: on a machine with both an AMD iGPU and
    /// an NVIDIA dGPU, most games run on the NVIDIA card, and every toggle on
    /// this page would appear to do nothing. Saying so plainly is more useful
    /// than a setting that silently has no effect.
    /// </summary>
    public static class RendererPolicy
    {
        /// <summary>The heading, e.g. "Rendering on the NVIDIA card".</summary>
        public static RendererVerdict Evaluate(bool hasNvidia, bool hasAmd, RendererChoice? preference)
        {
            if (!hasAmd)
            {
                return new RendererVerdict(
                    "No AMD adapter",
                    "There is no AMD GPU on this machine, so the Radeon settings have nothing to apply to.",
                    SettingsApply: false);
            }

            if (preference == RendererChoice.HighPerformance && hasNvidia)
            {
                return new RendererVerdict(
                    "Forced to the NVIDIA card",
                    "Windows is set to render this app on the high-performance (NVIDIA) adapter. " +
                    "Every setting on this page is an AMD driver control and will not affect it. " +
                    "Change it in Settings > System > Display > Graphics > Options.",
                    SettingsApply: false);
            }

            if (preference == RendererChoice.PowerSaving)
            {
                return new RendererVerdict(
                    "Forced to the Radeon iGPU",
                    "Windows is set to render this app on the power-saving adapter, which is the " +
                    "AMD one here. The settings on this page apply to it.",
                    SettingsApply: true);
            }

            if (hasNvidia)
            {
                return new RendererVerdict(
                    "Windows chooses (usually the NVIDIA card)",
                    "No per-app preference is set, so Windows decides. On a machine with both an " +
                    "AMD iGPU and an NVIDIA card, games almost always land on the NVIDIA one, and " +
                    "these settings will have no effect on them. Set the app to the power-saving " +
                    "adapter in Settings > System > Display > Graphics to use them.",
                    SettingsApply: false);
            }

            return new RendererVerdict(
                "Rendering on the Radeon GPU",
                "This machine has no competing discrete GPU, so anything drawing with the AMD " +
                "adapter is covered by these settings.",
                SettingsApply: true);
        }

        /// <summary>
        /// The warning shown when the machine simply has both vendors, regardless
        /// of which app was checked. Null when there is nothing to warn about.
        /// </summary>
        public static string? MixedVendorWarning(bool hasNvidia, bool hasAmd) =>
            hasNvidia && hasAmd
                ? "This machine has both an AMD and an NVIDIA GPU. Most games render on the NVIDIA " +
                  "card, where AMD's 3D settings have no effect. Check the active renderer above for " +
                  "the app you care about."
                : null;
    }

    /// <summary>
    /// Which GPU vendors are actually installed, used to decide which tabs the
    /// Graphics page offers. Pure: it takes the PNP device ids and works out the
    /// answer, so it is testable without hardware.
    /// </summary>
    public sealed record GpuVendorFlags(bool HasNvidia, bool HasAmd, IReadOnlyList<string> Adapters)
    {
        public bool AnyGpu => HasNvidia || HasAmd;

        public string Summary
        {
            get
            {
                var parts = new List<string>(2);
                if (HasAmd) parts.Add("AMD");
                if (HasNvidia) parts.Add("NVIDIA");
                return parts.Count == 0 ? "No display adapter detected" : string.Join(" + ", parts);
            }
        }

        /// <summary>
        /// Classifies display adapters from their PCI ids.
        ///
        /// Only the vendor id is read, and two families are excluded so the
        /// companions that share a GPU's vendor id cannot be mistaken for a
        /// second graphics card: AMD's chipset/SMBus controller (VEN_1022), and
        /// any function whose PCI class is not a display class (CC_030x). The
        /// caller is expected to feed this display-class adapters in the first
        /// place - it reads them from the display class registry key - and this
        /// second filter is what keeps an audio endpoint out.
        /// </summary>
        public static GpuVendorFlags FromPnpIds(IEnumerable<string?> pnpIds)
        {
            bool nvidia = false, amd = false;
            var adapters = new List<string>();

            foreach (string? id in pnpIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                string upper = id.ToUpperInvariant();
                if (!upper.StartsWith("PCI\\", StringComparison.Ordinal)) continue;

                // VEN_1022 is the AMD chipset/SMBus controller, never a display
                // part; it shares the vendor id and would show a phantom Radeon.
                if (upper.Contains("VEN_1022&", StringComparison.Ordinal)) continue;

                bool isNvidia = upper.Contains("VEN_10DE&", StringComparison.Ordinal);
                bool isAmd = upper.Contains("VEN_1002&", StringComparison.Ordinal);
                if (!isNvidia && !isAmd) continue;

                // CC_0300 / 0302 / 0303 / 0380 are the PCI display-class codes.
                // A function carrying ANY other class code is not the graphics
                // part, however familiar its vendor id looks. Ids with no class
                // code at all are accepted: the enumerator omits it on some
                // driverless machines, and refusing those would hide a real card.
                if (TryReadPciClassCode(upper, out string classCode) && !IsDisplayClassCode(classCode))
                    continue;

                if (isNvidia) nvidia = true;
                else amd = true;
                adapters.Add(id);
            }

            return new GpuVendorFlags(nvidia, amd, adapters.Distinct().ToList());
        }

        /// <summary>The PCI base-class/subclass pairs that are display controllers.</summary>
        private static bool IsDisplayClassCode(string classCode)
            => classCode is "0300" or "0302" or "0303" or "0380";

        /// <summary>Pulls the CC_xxxx PCI class code out of a PNP id, if it carries one.</summary>
        private static bool TryReadPciClassCode(string upperId, out string classCode)
        {
            int at = upperId.IndexOf("CC_", StringComparison.Ordinal);
            classCode = at >= 0 && at + 8 <= upperId.Length
                ? upperId.Substring(at + 3, 4)
                : string.Empty;
            return at >= 0;
        }
    }
}
