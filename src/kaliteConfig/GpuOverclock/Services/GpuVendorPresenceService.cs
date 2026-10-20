using System;
using System.Collections.Generic;
using System.IO;
using kaliteConfig.GpuOverclock.Models;
using Microsoft.Win32;

namespace kaliteConfig.GpuOverclock.Services
{
    public static class GpuVendorPresenceService
    {
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        private static GpuVendorFlags? _cached;

        public static GpuVendorFlags Detect()
        {
            if (_cached is GpuVendorFlags cached) return cached;
            GpuVendorFlags flags = ReadFromRegistry();
            _cached = flags;
            return flags;
        }

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
            }

            GpuVendorFlags fromRegistry = GpuVendorFlags.FromPnpIds(ids);
            if (fromRegistry.AnyGpu) return fromRegistry;

            return GpuVendorFlags.FromPnpIds(ProbePnpIdsViaWmi()) is { AnyGpu: true } viaWmi
                ? viaWmi
                : fromRegistry;
        }

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

        public static RendererChoice? GetPreferenceForApp(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string trimmed = input.Trim().Trim('"');

            if (trimmed.Contains('\\') || trimmed.Contains('/'))
                return GetPreferenceFor(trimmed);

            return GetPreferenceFor(ResolveInstalledPath(trimmed));
        }

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

        public static RendererVerdict EvaluateRenderer(GpuVendorFlags vendors, string? app)
            => RendererPolicy.Evaluate(vendors.HasNvidia, vendors.HasAmd, GetPreferenceForApp(app));
    }
}
