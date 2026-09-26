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
using kaliteConfig.Models;
using kaliteConfig.Native;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    /// <summary>
    /// Maps between the Windows driver-store version format and NVIDIA's
    /// marketing version format.
    ///
    /// WMI/registry DriverVersion looks like "32.0.15.6614" while NVIDIA calls
    /// the same package "566.14". The mapping is the last 5 digits of the
    /// Windows version split as XXX.YY - e.g. "32.0.15.6614" → "56614" →
    /// "566.14". This is the same translation NVCleanstall uses and is stable
    /// across DCH drivers.
    /// </summary>
    public static class NvidiaVersionHelper
    {
        public static string? FromWmiVersion(string? wmiVersion)
        {
            if (string.IsNullOrWhiteSpace(wmiVersion)) return null;
            string digits = new string(wmiVersion.Where(char.IsDigit).ToArray());
            if (digits.Length < 5) return null;
            string last5 = digits[^5..];
            if (!last5.All(char.IsDigit)) return null;
            return $"{last5[..3]}.{last5[3..]}";
        }

        /// <summary>Numeric segment comparison: negative → a older than b.</summary>
        public static int Compare(string a, string b)
        {
            var segsA = a.Split('.');
            var segsB = b.Split('.');
            int n = Math.Max(segsA.Length, segsB.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < segsA.Length && int.TryParse(segsA[i], out int x) ? x : 0;
                int vb = i < segsB.Length && int.TryParse(segsB[i], out int y) ? y : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }
    }

    public sealed record DetectedGpu(string Name, string Vendor, string DriverVersion, string VideoProcessor = "", string Status = "")
    {
        public string VramText { get; init; } = string.Empty;
        public string GpuType { get; init; } = string.Empty;   // Discrete / Integrated
        public string DeviceType { get; init; } = string.Empty; // Display
        public bool IsPrimary { get; init; }

        /// <summary>
        /// Driver version read directly from the adapter's registry class key
        /// (driver store), used to cross-check the WMI-reported version.
        /// Null when the registry entry wasn't found.
        /// </summary>
        public string? RegistryVersion { get; init; }

        /// <summary>
        /// PnP device ID (PCI\VEN_…&DEV_…), when known. Lets the driver lookup
        /// query by hardware device ID instead of the marketing name - the only
        /// reliable path for a driverless card Windows can't name.
        /// </summary>
        public string? PnpDeviceId { get; init; }
    }

    public class GpuDriverService
    {
        private static readonly HttpClient _httpClient = new();

        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        // NVIDIA Lookup API series pairs (psid, pfid). Any valid desktop series
        // returns the current Game Ready driver; tried in order until one succeeds.
        private static readonly (int Psid, int Pfid)[] NvidiaSeriesQueries = new[]
        {
            (120, 929),   // RTX 30 desktop
            (127, 1039),  // RTX 40 desktop
            (112, 895),   // GTX 16 desktop
            (107, 879),   // RTX 20 desktop
        };

        /// <summary>
        /// Enumerates display adapters using robust WMI Win32_VideoController queries.
        /// Vendor comes from the PCI hardware ID (VEN_xxxx), never from the device
        /// name string, and only present, problem-free adapters count - WMI also
        /// reports disabled/phantom iGPUs (e.g. AMD Radeon Graphics next to an
        /// NVIDIA dGPU) which would otherwise show stale hardware.
        /// </summary>
        public Task<List<DetectedGpu>> DetectGpusAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var result = new List<DetectedGpu>();
                try
                {
                    var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                    foreach (var obj in searcher.Get())
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = obj["Name"]?.ToString() ?? "Unknown GPU";
                        string driverVersion = obj["DriverVersion"]?.ToString() ?? "Unknown";
                        string videoProcessor = obj["VideoProcessor"]?.ToString() ?? name;

                        string vramStr = obj["AdapterRAM"]?.ToString() ?? "0";
                        long.TryParse(vramStr, out long vramRaw);

                        string status = obj["Status"]?.ToString() ?? "OK";
                        uint configError = 0;
                        try
                        {
                            // Non-zero means the adapter has a problem code (disabled,
                            // failed start, phantom...). Only healthy adapters count.
                            configError = Convert.ToUInt32(obj["ConfigManagerErrorCode"] ?? 0);
                        }
                        catch { }

                        string vendor = VendorFromPnpId(obj["PNPDeviceID"]?.ToString());
                        if (vendor == "Unknown")
                            vendor = VendorFromName(name);

                        if (vendor == "Unknown") continue;

                        // Keep the adapter visible when it has a problem code.
                        // Code 28 (drivers not installed) is exactly the state this
                        // page exists to fix - a driverless GPU must still show up
                        // so the user can install one. Same for code 31/43 (driver
                        // failed to load). Only skip genuinely absent hardware.
                        bool hasNoDriver = configError is 28 or 31 or 43;
                        if (configError != 0 && !hasNoDriver)
                        {
                            Debug.WriteLine($"DetectGpus: skipping {name} (ConfigManagerErrorCode={configError})");
                            continue;
                        }
                        if (!hasNoDriver && !NativeMethods.CfgMgr32.IsDevicePresent(obj["PNPDeviceID"]?.ToString() ?? ""))
                        {
                            Debug.WriteLine($"DetectGpus: skipping non-present adapter {name}");
                            continue;
                        }

                        // AdapterRAM is a 32-bit uint in WMI, so cards with >4 GB
                        // saturate at ~0xFFFE0000 (4293918720), not uint.MaxValue.
                        // Treat anything near the 4 GB ceiling (or zero) as clamped.
                        long vramBytes = vramRaw;
                        bool looksClamped = vramBytes <= 0 ||
                            (vramBytes > (4L * 1024 * 1024 * 1024) - (128L * 1024 * 1024));
                        if (looksClamped)
                            vramBytes = ReadVramFromRegistry(obj["PNPDeviceID"]?.ToString() ?? "");
                        string vramText = FormatVram(vramBytes);

                        bool integrated =
                            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("(TM) Graphics", StringComparison.OrdinalIgnoreCase) || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) &&
                            !name.Contains("RX ", StringComparison.OrdinalIgnoreCase);
                        if (vendor == "Intel" && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            integrated = true;

                        string pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                        // Driverless adapters report no DriverVersion; give the
                        // ViewModel a marker it can map to the NotInstalled state.
                        if (hasNoDriver)
                            driverVersion = string.Empty;

                        // Cross-check: the display class key holds the same driver
                        // version the driver store actually loaded. A disagreement
                        // between WMI and the registry usually means a pending
                        // update (installed but not yet active) - surface both.
                        string? registryVersion = vendor == "NVIDIA" && !string.IsNullOrEmpty(pnpId)
                            ? ReadRegistryDriverVersion(pnpId)
                            : null;

                        result.Add(new DetectedGpu(name, vendor, driverVersion, videoProcessor, status)
                        {
                            VramText = vramText,
                            GpuType = integrated ? "Integrated" : "Discrete",
                            DeviceType = "Display",
                            IsPrimary = false, // set by caller after ordering
                            RegistryVersion = registryVersion,
                            PnpDeviceId = pnpId,
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DetectGpus (WMI): {ex.Message}");
                }

                // Fallback: with no driver loaded, some systems never surface the
                // adapter in Win32_VideoController (and PNPClass can differ).
                // Two-stage fallback:
                //   1. Win32_PnPEntity where PNPClass='Display'
                //   2. Win32_PnPEntity matched by PCI vendor ID (VEN_10DE/1002/
                //      8086) - pure hardware identity, works with no driver at all.
                if (result.Count == 0)
                {
                    try
                    {
                        var query = new System.Management.ManagementObjectSearcher(
                            "SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                            "WHERE PNPClass = 'Display' OR DeviceID LIKE 'PCI%VEN_10DE%' " +
                            "OR DeviceID LIKE 'PCI%VEN_1002%' OR DeviceID LIKE 'PCI%VEN_1022%' " +
                            "OR DeviceID LIKE 'PCI%VEN_8086%'");
                        foreach (var obj in query.Get())
                        {
                            ct.ThrowIfCancellationRequested();
                            string name = obj["Name"]?.ToString() ?? "Unknown GPU";
                            string pnpId = obj["DeviceID"]?.ToString() ?? "";
                            string? pnpClass = obj["PNPClass"]?.ToString();
                            uint code = 0;
                            try { code = Convert.ToUInt32(obj["ConfigManagerErrorCode"] ?? 0); } catch { }

                            string vendor = VendorFromPnpId(pnpId);
                            if (vendor == "Unknown") vendor = VendorFromName(name);
                            if (vendor == "Unknown") continue;

                            bool isDisplayClass = pnpClass?.Equals("Display", StringComparison.OrdinalIgnoreCase) == true;

                            // PCI class code in the DeviceID when present:
                            // CC_0300/0301/0380 = display controllers.
                            bool isPciDisplay = System.Text.RegularExpressions.Regex.IsMatch(
                                pnpId, @"CC_03[0-9A-F]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                            // Real-world case (verified on a driverless machine):
                            // the GPU enumerates as PCI\VEN_10DE&DEV_xxxx with
                            // Name exactly "Display", an EMPTY PNPClass, and no
                            // CC_ suffix. Accept that shape explicitly.
                            bool isUnnamedDisplayAdapter =
                                pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) &&
                                name.Trim().Equals("Display", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(pnpClass, "MEDIA", StringComparison.OrdinalIgnoreCase);

                            // Never accept audio/SMBus/etc. companions that share
                            // the GPU vendor ID (NVIDIA HD Audio, AMD SMBus…).
                            bool isCompanionDevice =
                                name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("High Definition", StringComparison.OrdinalIgnoreCase) ||
                                pnpClass?.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) == true;

                            if (isCompanionDevice) continue;
                            if (!isDisplayClass && !isPciDisplay && !isUnnamedDisplayAdapter)
                                continue;
                            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                                continue; // generic fallback renderer, not real hardware identity

                            Debug.WriteLine($"DetectGpus: PnPEntity fallback found {name} (code={code}, class={pnpClass})");
                            result.Add(new DetectedGpu(name, vendor, "", name, "OK")
                            {
                                PnpDeviceId = pnpId,
                                GpuType = name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) &&
                                          !name.Contains("RX ", StringComparison.OrdinalIgnoreCase)
                                    ? "Integrated" : "Discrete",
                                DeviceType = "Display",
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DetectGpus (PnPEntity fallback): {ex.Message}");
                    }
                }

                return result;
            }, ct);
        }

        /// <summary>
        /// Win32_VideoController.AdapterRAM is a UInt32 and saturates above 4 GB.
        /// The display class registry key holds the true value in
        /// HardwareInformation.qwMemorySize (QWORD, bytes).
        /// </summary>
        private static long ReadVramFromRegistry(string pnpId)
        {
            try
            {
                if (string.IsNullOrEmpty(pnpId)) return 0;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    DisplayClassKey, writable: false);
                if (key is null) return 0;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is null) continue;

                    // MatchingDeviceId is the enumerator-relative ID (e.g.
                    // "PCI\VEN_10DE&DEV_2704..."), the PNPDeviceID adds the instance
                    // suffix - match by prefix.
                    if (child.GetValue("MatchingDeviceId")?.ToString() is not string matching ||
                        !pnpId.StartsWith(matching, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (string valueName in new[]
                        { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
                    {
                        object? raw = child.GetValue(valueName);
                        long parsed = raw switch
                        {
                            int i => i,
                            uint u => u,
                            long l => l,
                            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                            byte[] bytes when bytes.Length == 4 => BitConverter.ToInt32(bytes, 0),
                            string s when long.TryParse(s, out long v) => v,
                            _ => 0,
                        };
                        if (parsed > 0) return parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadVramFromRegistry: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Reads DriverVersion from the adapter's display-class registry subkey
        /// (matched by MatchingDeviceId prefix, same trick as VRAM). This is the
        /// value the currently-loaded driver store entry reports - the registry
        /// half of the WMI-vs-registry version cross-check.
        /// </summary>
        private static string? ReadRegistryDriverVersion(string pnpId)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    DisplayClassKey, writable: false);
                if (key is null) return null;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is null) continue;
                    if (child.GetValue("MatchingDeviceId")?.ToString() is not string matching ||
                        !pnpId.StartsWith(matching, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? version = child.GetValue("DriverVersion")?.ToString();
                    return string.IsNullOrWhiteSpace(version) ? null : version;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadRegistryDriverVersion: {ex.Message}");
            }
            return null;
        }

        private static string FormatVram(long bytes)
        {
            if (bytes <= 0) return "Unknown";
            double gb = bytes / (1024.0 * 1024 * 1024);
            return gb >= 1.0 ? $"{gb:0} GB" : $"{bytes / (1024.0 * 1024):0} MB";
        }

        // PCI vendor IDs: 10DE = NVIDIA, 1002 = AMD/ATI, 8086 = Intel.
        private static string VendorFromPnpId(string? pnpId)
        {
            if (string.IsNullOrEmpty(pnpId)) return "Unknown";
            if (pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || pnpId.Contains("VEN_1022")) return "AMD";
            if (pnpId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            return "Unknown";
        }

        // Fallback for non-PCI adapters (USB display, hypervisor adapters...).
        private static string VendorFromName(string name)
        {
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("ATI", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "Intel";
            return "Unknown";
        }

        /// <summary>
        /// Latest NVIDIA Game Ready (WHQL/DCH) driver via the same lookup API
        /// tools like NVCleanstall use. Returns (version, directExeUrl) or nulls
        /// when offline or when the response shape changes - callers fall back
        /// to the official download page.
        ///
        /// ACCURACY FLAG: gfwsl.geforce.com AjaxDriverService.php is an
        /// UNDOCUMENTED, reverse-engineered endpoint (not an official NVIDIA
        /// API). It can change or disappear without notice; callers must treat
        /// failure as "unable to check", never as ground truth.
        /// </summary>
        public async Task<(string? Version, string? Url)> GetLatestNvidiaDriverAsync(CancellationToken ct)
        {
            foreach (var (psid, pfid) in NvidiaSeriesQueries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string api = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    string json = await _httpClient.GetStringAsync(api, cts.Token);
                    var (version, url) = ParseNvidiaLookup(json);
                    if (!string.IsNullOrEmpty(version) && IsHttpUrl(url))
                        return (version, url);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-series HTTP timeout: same backend serves all series, stop trying.
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"NvidiaLookup {psid}/{pfid}: {ex.Message}");
                }
            }
            return (null, null);
        }

        private static (string? Version, string? Url) ParseNvidiaLookup(string json)
        {
            try
            {
                var root = JsonNode.Parse(json);
                var ids = root?["IDS"]?.AsArray();
                if (ids is null || ids.Count == 0) return (null, null);
                var first = ids[0];
                var info = first?["downloadInfo"] ?? first;
                string? version = info?["Version"]?.GetValue<string>();
                string? url = info?["DownloadURL"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    url = first?["DownloadURL"]?.GetValue<string>();
                return (version?.Trim(), url?.Trim());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ParseNvidiaLookup: {ex.Message}");
                return (null, null);
            }
        }

        private static bool IsHttpUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        public static void OpenUrl(string? url)
        {
            try
            {
                if (!IsHttpUrl(url)) return;
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenUrl: {ex.Message}");
            }
        }

        /// <summary>
        /// Full silent pipeline for NVIDIA: download with progress, run the vendor
        /// installer silent (-s -noreboot), then verify via exit code. Mirrors
        /// InstallerService.InstallBrowserAsync (elevated manifest, UseShellExecute=false).
        /// </summary>
        public async Task InstallNvidiaAsync(GpuDriverItem item, IProgress<GpuDriverStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct)
        {
            progress.Report(GpuDriverStatus.Downloading);
            downloadProgress.Report(0);
            string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
            try
            {
                using var response = await _httpClient.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                var lastReport = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                    if (totalBytes != -1 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        downloadProgress.Report((double)totalRead / totalBytes * 100.0);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Download failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            ct.ThrowIfCancellationRequested();

            progress.Report(GpuDriverStatus.Installing);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = item.SilentInstallArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    errorProgress.Report("Installer failed to start.");
                    progress.Report(GpuDriverStatus.Failed);
                    return;
                }

                // Driver packages take several minutes; 20-minute ceiling, then kill.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    errorProgress.Report("Installer timed out after 20 minutes and was terminated.");
                    progress.Report(GpuDriverStatus.Failed);
                    return;
                }

                int exit = -1;
                try { if (process.HasExited) exit = process.ExitCode; } catch { }
                Debug.WriteLine($"NVIDIA installer exit code {exit}");
                if (exit == 0)
                {
                    await RefreshInstalledVersionAsync(item, "NVIDIA", ct);
                    progress.Report(GpuDriverStatus.Installed);
                }
                else
                {
                    errorProgress.Report($"Installer exited with code {exit}. A reboot may be pending - check GeForce Experience.");
                    progress.Report(GpuDriverStatus.Failed);
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                errorProgress.Report($"Install failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            CleanUp(tempPath);
        }

        /// <summary>
        /// Guided path for vendors without reliable silent flags: download the
        /// package (when a URL is known) then hand off to the vendor UI.
        /// </summary>
        public async Task DownloadAndLaunchAsync(GpuDriverItem item, IProgress<GpuDriverStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct)
        {
            if (!IsHttpUrl(item.DownloadUrl))
            {
                OpenUrl(item.VendorPageUrl);
                progress.Report(GpuDriverStatus.ManualActionRequired);
                return;
            }

            progress.Report(GpuDriverStatus.Downloading);
            downloadProgress.Report(0);
            string tempPath = Path.Combine(Path.GetTempPath(), item.InstallerFileName);
            try
            {
                using var response = await _httpClient.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                var lastReport = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                    if (totalBytes != -1 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        downloadProgress.Report((double)totalRead / totalBytes * 100.0);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CleanUp(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                CleanUp(tempPath);
                errorProgress.Report($"Download failed: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                progress.Report(GpuDriverStatus.ManualActionRequired);
            }
            catch (Exception ex)
            {
                errorProgress.Report($"Could not launch installer: {ex.Message}");
                progress.Report(GpuDriverStatus.Failed);
            }
        }

        public async Task RefreshInstalledVersionAsync(GpuDriverItem item, string vendor, CancellationToken ct = default)
        {
            try
            {
                var gpus = await DetectGpusAsync(ct);
                foreach (var gpu in gpus)
                {
                    if (gpu.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(gpu.DriverVersion) &&
                        !gpu.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    {
                        item.InstalledVersion = gpu.DriverVersion;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshInstalledVersion: {ex.Message}");
            }
        }

        private static void CleanUp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
