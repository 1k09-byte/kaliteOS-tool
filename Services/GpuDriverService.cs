using stellarisKIT.Models;
using stellarisKIT.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services
{
    public sealed record DetectedGpu(string Name, string Vendor, string DriverVersion, string VideoProcessor = "", string Status = "")
    {
        public string VramText { get; init; } = string.Empty;
        public string GpuType { get; init; } = string.Empty;   // Discrete / Integrated
        public string DeviceType { get; init; } = string.Empty; // Display
        public bool IsPrimary { get; init; }
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
        /// name string, and only present, problem-free adapters count — WMI also
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
                        if (configError != 0)
                        {
                            Debug.WriteLine($"DetectGpus: skipping {name} (ConfigManagerErrorCode={configError})");
                            continue;
                        }
                        if (!NativeMethods.CfgMgr32.IsDevicePresent(obj["PNPDeviceID"]?.ToString() ?? ""))
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
                        result.Add(new DetectedGpu(name, vendor, driverVersion, videoProcessor, status)
                        {
                            VramText = vramText,
                            GpuType = integrated ? "Integrated" : "Discrete",
                            DeviceType = "Display",
                            IsPrimary = false, // set by caller after ordering
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DetectGpus (WMI): {ex.Message}");
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
                    // suffix — match by prefix.
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
        /// when offline or when the response shape changes — callers fall back
        /// to the official download page.
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
                    errorProgress.Report($"Installer exited with code {exit}. A reboot may be pending — check GeForce Experience.");
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
