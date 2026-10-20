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
        public string GpuType { get; init; } = string.Empty;
        public string DeviceType { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }

        public string? RegistryVersion { get; init; }

        public string? PnpDeviceId { get; init; }
    }

    public class GpuDriverService
    {
        private static readonly HttpClient _httpClient = new();

        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static readonly (int Psid, int Pfid)[] NvidiaSeriesQueries = new[]
        {
            (120, 929),
            (127, 1039),
            (112, 895),
            (107, 879),
        };

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
                            configError = Convert.ToUInt32(obj["ConfigManagerErrorCode"] ?? 0);
                        }
                        catch { }

                        string vendor = VendorFromPnpId(obj["PNPDeviceID"]?.ToString());
                        if (vendor == "Unknown")
                            vendor = VendorFromName(name);

                        if (vendor == "Unknown") continue;

                        bool hasNoDriver = configError is 28 or 31 or 43;
                        if (configError != 0 && !hasNoDriver)
                        {
                            continue;
                        }
                        if (!hasNoDriver && !NativeMethods.CfgMgr32.IsDevicePresent(obj["PNPDeviceID"]?.ToString() ?? ""))
                        {
                            continue;
                        }

                        long vramBytes = vramRaw;
                        bool looksClamped = vramBytes <= 0 ||
                            (vramBytes > (4L * 1024 * 1024 * 1024) - (128L * 1024 * 1024));
                        if (looksClamped)
                            vramBytes = ReadVramFromRegistry(obj["PNPDeviceID"]?.ToString() ?? "");
                        if (vramBytes <= 0 && vendor == "AMD")
                        {
                            vramBytes = ReadVramFromAdlx(obj["PNPDeviceID"]?.ToString() ?? "");
                        }
                        string vramText = FormatVram(vramBytes);

                        bool integrated =
                            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("(TM) Graphics", StringComparison.OrdinalIgnoreCase) || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) &&
                            !name.Contains("RX ", StringComparison.OrdinalIgnoreCase);
                        if (vendor == "Intel" && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            integrated = true;

                        string pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                        if (hasNoDriver)
                            driverVersion = string.Empty;

                        string? registryVersion = vendor == "NVIDIA" && !string.IsNullOrEmpty(pnpId)
                            ? ReadRegistryDriverVersion(pnpId)
                            : null;

                        result.Add(new DetectedGpu(name, vendor, driverVersion, videoProcessor, status)
                        {
                            VramText = vramText,
                            GpuType = integrated ? "Integrated" : "Discrete",
                            DeviceType = "Display",
                            IsPrimary = false,
                            RegistryVersion = registryVersion,
                            PnpDeviceId = pnpId,
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                }

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

                            bool isPciDisplay = System.Text.RegularExpressions.Regex.IsMatch(
                                pnpId, @"CC_03[0-9A-F]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                            bool isUnnamedDisplayAdapter =
                                pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) &&
                                name.Trim().Equals("Display", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(pnpClass, "MEDIA", StringComparison.OrdinalIgnoreCase);

                            bool isCompanionDevice =
                                name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("High Definition", StringComparison.OrdinalIgnoreCase) ||
                                pnpClass?.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) == true;

                            if (isCompanionDevice) continue;
                            if (!isDisplayClass && !isPciDisplay && !isUnnamedDisplayAdapter)
                                continue;
                            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                                continue;

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
                    catch (Exception)
                    {
                    }
                }

                return result;
            }, ct);
        }

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

                    if (child.GetValue("MatchingDeviceId")?.ToString() is not string matching ||
                        !MatchesPnpId(matching, pnpId))
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
            catch (Exception)
            {
            }
            return 0;
        }

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
                        !MatchesPnpId(matching, pnpId))
                        continue;

                    string? version = child.GetValue("DriverVersion")?.ToString();
                    return string.IsNullOrWhiteSpace(version) ? null : version;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static long ReadVramFromAdlx(string pnpId)
        {
            try
            {
                var gpus = kaliteConfig.GpuOverclock.Services.AmdAdlxInterop.EnumerateGpusWithSupport();
                foreach (var g in gpus)
                {
                    if (g.VramMb == 0) continue;
                    if (!MatchesPnpId(g.PnpString, pnpId)) continue;
                    return (long)g.VramMb * 1024 * 1024;
                }
            }
            catch { }
            return 0;
        }

        private static bool MatchesPnpId(string matching, string pnpId)
        {
            if (string.IsNullOrEmpty(matching) || string.IsNullOrEmpty(pnpId)) return false;
            if (pnpId.StartsWith(matching, StringComparison.OrdinalIgnoreCase)) return true;
            if (matching.StartsWith(pnpId, StringComparison.OrdinalIgnoreCase)) return true;

            static string Core(string s)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    s, @"ven_[0-9a-f]{4}[&_]dev_[0-9a-f]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Value.ToLowerInvariant() : s.Replace('\\', '_').ToLowerInvariant();
            }
            return string.Equals(Core(matching), Core(pnpId), StringComparison.Ordinal);
        }

        private static string FormatVram(long bytes)
        {
            if (bytes <= 0) return "Unknown";
            double gb = bytes / (1024.0 * 1024 * 1024);
            return gb >= 1.0 ? $"{gb:0} GB" : $"{bytes / (1024.0 * 1024):0} MB";
        }

        private static string VendorFromPnpId(string? pnpId)
        {
            if (string.IsNullOrEmpty(pnpId)) return "Unknown";
            if (pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || pnpId.Contains("VEN_1022")) return "AMD";
            if (pnpId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            return "Unknown";
        }

        private static string VendorFromName(string name)
        {
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("ATI", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "Intel";
            return "Unknown";
        }

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
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
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
            catch (Exception)
            {
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
            catch (Exception)
            {
            }
        }

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
                        item.InstalledVersion = string.IsNullOrWhiteSpace(gpu.RegistryVersion)
                            ? gpu.DriverVersion
                            : gpu.RegistryVersion;
                        return;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void CleanUp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
