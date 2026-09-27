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
using System.IO;
using kaliteConfig.GpuOverclock.Models;
using Microsoft.Win32;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Answers two questions the Graphics page needs before it draws anything:
    /// which GPU vendors are actually installed, and which GPU Windows will
    /// render a given program on.
    ///
    /// Both come from Windows' own state rather than from the app guessing:
    ///
    ///   - The display-class registry key (the same one
    ///     GpuDriverService cross-checks driver versions against) holds one
    ///     subkey per installed display adapter, and its MatchingDeviceId is the
    ///     adapter's PCI hardware id. Reading it is a few hundred microseconds;
    ///     WMI, which GpuDriverService uses for the full detection on the Drivers
    ///     tab, takes hundreds of milliseconds and is far too slow to run while
    ///     a page is deciding which tabs to show.
    ///
    ///   - Per-app graphics preferences live in
    ///     HKCU\Software\Microsoft\DirectX\UserGpuPreferences, keyed by the full
    ///     executable path, with a value like "GpuPreference=2;". That is the
    ///     same store Settings > System > Display > Graphics writes.
    /// </summary>
    public static class GpuVendorPresenceService
    {
        /// <summary>The Windows display setup class GUID. Every display adapter
        /// with a bound driver has a subkey here.</summary>
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        private static GpuVendorFlags? _cached;

        /// <summary>
        /// Which vendors are present. Cached after the first call: the answer
        /// only changes when a driver is installed, and the Graphics page asks on
        /// every visit. Call <see cref="Invalidate"/> after installing one.
        /// </summary>
        public static GpuVendorFlags Detect()
        {
            if (_cached is GpuVendorFlags cached) return cached;
            GpuVendorFlags flags = ReadFromRegistry();
            _cached = flags;
            return flags;
        }

        /// <summary>Forces the next <see cref="Detect"/> to re-read the hardware.</summary>
        public static void Invalidate() => _cached = null;

        private static GpuVendorFlags ReadFromRegistry()
        {
            var ids = new List<string>();
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                if (root is not null)
                {
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using RegistryKey? child = root.OpenSubKey(sub);
                        if (child?.GetValue("MatchingDeviceId") is string id && !string.IsNullOrWhiteSpace(id))
                            ids.Add(id);
                    }
                }
            }
            catch (Exception)
            {
                // A locked-down or missing key just means we fall through to the
                // WMI probe below, which is slower but works everywhere.
            }

            GpuVendorFlags fromRegistry = GpuVendorFlags.FromPnpIds(ids);
            if (fromRegistry.AnyGpu) return fromRegistry;

            return GpuVendorFlags.FromPnpIds(ProbePnpIdsViaWmi()) is { AnyGpu: true } viaWmi
                ? viaWmi
                : fromRegistry;
        }

        /// <summary>
        /// Fallback for a machine where the display class key is empty (no driver
        /// bound, or the key is unreadable). Slow, so it only runs when the fast
        /// path found nothing.
        /// </summary>
        private static IEnumerable<string> ProbePnpIdsViaWmi()
        {
            var ids = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT PNPDeviceID FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    if (obj["PNPDeviceID"]?.ToString() is string id && !string.IsNullOrWhiteSpace(id))
                        ids.Add(id);
                }
            }
            catch (Exception) { }
            return ids;
        }

        /// <summary>
        /// Windows' per-app graphics preference for one executable, or null when
        /// the app has never been assigned one.
        /// </summary>
        public static RendererChoice? GetPreferenceFor(string? executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return null;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey);
                return key?.GetValue(executablePath.Trim()) is string data
                    ? GpuPreferenceParser.ParsePreference(data)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The per-app preference for a program the user named. A bare name is
        /// resolved against the installed app if it can be found, because the
        /// registry is keyed by the full path.
        /// </summary>
        public static RendererChoice? GetPreferenceForApp(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string trimmed = input.Trim().Trim('"');

            if (trimmed.Contains('\\') || trimmed.Contains('/'))
                return GetPreferenceFor(trimmed);

            // Resolved against the install locations; if nothing is there the
            // bare name will not be a key in the registry either, which is the
            // honest answer: that app has no per-app preference.
            return GetPreferenceFor(ResolveInstalledPath(trimmed));
        }

        /// <summary>Looks an executable up in the usual install locations.</summary>
        private static string ResolveInstalledPath(string executableName)
        {
            string fileName = executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? executableName
                : executableName + ".exe";

            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] candidates =
            {
                Path.Combine(system, fileName),
                Path.Combine(programFiles, fileName),
                Path.Combine(programFilesX86, fileName),
                Path.Combine(localAppData, fileName),
                Path.Combine(windows, fileName),
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception) { }
            }

            return fileName;
        }

        /// <summary>
        /// What the Radeon tab should say about an app: which GPU it renders on,
        /// and therefore whether these settings can affect it at all.
        /// </summary>
        public static RendererVerdict EvaluateRenderer(GpuVendorFlags vendors, string? app)
            => RendererPolicy.Evaluate(vendors.HasNvidia, vendors.HasAmd, GetPreferenceForApp(app));
    }
}
