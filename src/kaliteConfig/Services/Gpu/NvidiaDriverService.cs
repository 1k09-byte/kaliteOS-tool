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
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace kaliteConfig.Services
{
    public record NvidiaDriverPackage(string Version, string DownloadUrl, string ReleaseDateTime);

    public class NvidiaDriverService
    {
        private static readonly HttpClient _httpClient = new();
        private static Dictionary<string, string> _pfidCache = new(StringComparer.OrdinalIgnoreCase);

        private async Task<string?> GetPfidFromModelStringAsync(string gpuModelName, bool notebookVariant, CancellationToken ct)
        {
            bool wmiIsLaptop = gpuModelName.Contains("Laptop", StringComparison.OrdinalIgnoreCase);
            string stripped = gpuModelName.Replace(" Laptop GPU", "", StringComparison.OrdinalIgnoreCase)
                                          .Replace(" (Notebook)", "", StringComparison.OrdinalIgnoreCase).Trim();
            var candidates = new List<string> { gpuModelName };
            if (notebookVariant && !wmiIsLaptop)
                candidates.Add(stripped + " Laptop GPU");
            else if (!notebookVariant && wmiIsLaptop)
                candidates.Add(stripped);

            foreach (var candidate in candidates)
                if (_pfidCache.TryGetValue(candidate, out var cached))
                    return cached;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                
                string lookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
                string xmlResponse = await _httpClient.GetStringAsync(lookupUrl, cts.Token);
                
                var doc = XDocument.Parse(xmlResponse);
                var lookupValues = doc.Descendants("LookupValue");

                foreach (var node in lookupValues)
                {
                    string? name = node.Element("Name")?.Value;
                    string? val = node.Element("Value")?.Value;
                    
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
                    {
                        foreach (var candidate in candidates)
                        {
                            if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var c in candidates) _pfidCache[c] = val;
                                return val;
                            }
                        }
                    }
                }

                foreach (var node in lookupValues)
                {
                    string? name = node.Element("Name")?.Value;
                    string? val = node.Element("Value")?.Value;
                    
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
                    {
                        if (ModelMatchesFamily(gpuModelName, name))
                        {
                            foreach (var c in candidates) _pfidCache[c] = val;
                            return val;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static bool ModelMatchesFamily(string model, string family)
        {
            model = model.ToUpperInvariant();
            family = family.ToUpperInvariant().Replace(" SERIES", "");

            var familyTokens = family.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int matchCount = 0;

            foreach (var token in familyTokens)
            {
                if (token == "NVIDIA" || token == "GEFORCE") continue;

                int idx = model.IndexOf(token);
                if (idx != -1) 
                {
                    matchCount++;
                }
            }

            List<string> keyTokens = familyTokens.Where(t => t != "NVIDIA" && t != "GEFORCE" && t != "TITAN" && t != "MX").ToList();
            if (keyTokens.Count == 0) return false;
            
            bool isMatch = keyTokens.All(t => model.Contains(t));
            return isMatch;
        }

        private int GetOsId()
        {
            var build = Environment.OSVersion.Version.Build;
            return build >= 22000 ? 135 : 57;
        }

        public async Task<List<NvidiaDriverPackage>> GetDriversAsync(string gpuModelName, int numberOfResults, bool studioChannel, CancellationToken ct)
            => await GetDriversAsync(gpuModelName, numberOfResults, studioChannel, notebookVariant: false, ct);

        public async Task<List<NvidiaDriverPackage>> GetDriversAsync(string gpuModelName, int numberOfResults, bool studioChannel, bool notebookVariant, CancellationToken ct)
        {
            var results = new List<NvidiaDriverPackage>();
            var pfid = await GetPfidFromModelStringAsync(gpuModelName, notebookVariant, ct);
            if (string.IsNullOrEmpty(pfid))
                return results;

            int osId = GetOsId();
            int upCrd = studioChannel ? 1 : 0;
            int isWhql = studioChannel ? 0 : 1;
            string apiUrl = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&pfid={pfid}&osID={osId}&languageCode=1033&isWHQL={isWhql}&beta=0&dltype=-1&dch=1&upCRD={upCrd}&ctk=null&sort1=0&numberOfResults={numberOfResults}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                string json = await _httpClient.GetStringAsync(apiUrl, cts.Token);
                var root = JsonNode.Parse(json);
                var ids = root?["IDS"]?.AsArray();
                if (ids == null) return results;

                foreach (var id in ids)
                {
                    var info = id?["downloadInfo"] ?? id;
                    string? version = info?["Version"]?.GetValue<string>();
                    string? url = info?["DownloadURL"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(url))
                        url = id?["DownloadURL"]?.GetValue<string>();
                    string? releaseDate = info?["ReleaseDateTime"]?.GetValue<string>();

                    if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(url))
                    {
                        results.Add(new NvidiaDriverPackage(version.Trim(), url.Trim(), releaseDate?.Trim() ?? ""));
                    }
                }
            }
            catch (Exception)
            {
            }
            return results;
        }

        public Task<List<NvidiaDriverPackage>> GetDriversAsync(string gpuModelName, int numberOfResults, CancellationToken ct)
            => GetDriversAsync(gpuModelName, numberOfResults, studioChannel: false, notebookVariant: false, ct);

        public async Task<List<NvidiaDriverPackage>> GetDriversByDeviceIdAsync(string pciDeviceId, int numberOfResults, bool studioChannel, CancellationToken ct)
        {
            var results = new List<NvidiaDriverPackage>();

            var m = System.Text.RegularExpressions.Regex.Match(
                pciDeviceId ?? "", @"DEV_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return results;
            string deviceId = m.Groups[1].Value;

            int osId = GetOsId();
            int upCrd = studioChannel ? 1 : 0;
            int isWhql = studioChannel ? 0 : 1;
            string apiUrl = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&deviceID={deviceId}&osID={osId}&languageCode=1033&isWHQL={isWhql}&beta=0&dltype=-1&dch=1&upCRD={upCrd}&ctk=null&sort1=0&numberOfResults={numberOfResults}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                string json = await _httpClient.GetStringAsync(apiUrl, cts.Token);
                var root = System.Text.Json.Nodes.JsonNode.Parse(json);
                var ids = root?["IDS"]?.AsArray();
                if (ids == null) return results;

                foreach (var id in ids)
                {
                    var info = id?["downloadInfo"] ?? id;
                    string? version = info?["Version"]?.GetValue<string>();
                    string? url = info?["DownloadURL"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(url))
                        url = id?["DownloadURL"]?.GetValue<string>();
                    string? releaseDate = info?["ReleaseDateTime"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(url))
                        results.Add(new NvidiaDriverPackage(version.Trim(), url.Trim(), releaseDate?.Trim() ?? ""));
                }
            }
            catch (Exception)
            {
            }
            return results;
        }

        private static IEnumerable<string> FindNvidiaDisplayInfs(string extractedDir)
        {
            if (!System.IO.Directory.Exists(extractedDir)) return Array.Empty<string>();
            var found = new List<string>();
            var displayDir = Path.Combine(extractedDir, "Display.Driver");
            foreach (var name in new[] { "nv_disp.inf", "nv_dispi.inf" })
            {
                var candidate = Path.Combine(displayDir, name);
                if (File.Exists(candidate)) found.Add(candidate);
            }
            foreach (var name in new[] { "nv_disp.inf", "nv_dispi.inf" })
            {
                found.AddRange(System.IO.Directory.GetFiles(extractedDir, name, System.IO.SearchOption.AllDirectories));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public async Task<bool> InstallSilentAsync(string driverExePath, bool debloat, Action<string> logCallback, CancellationToken ct)
        {
            try
            {
                var amdService = new AmdDriverService();
                string extractDir = Path.Combine(Path.GetTempPath(), "Nvidia_Extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);
                
                logCallback("Extracting NVIDIA payload via 7-Zip framework...");
                bool extracted = await amdService.ExtractInstallerAsync(driverExePath, extractDir, logCallback, ct);
                if (!extracted) {
                    logCallback("Failed to extract NVIDIA payload.");
                    return false;
                }

                var infs = FindNvidiaDisplayInfs(extractDir).ToList();
                if (infs.Count == 0) {
                    logCallback("No NVIDIA display INFs found (nv_disp.inf / nv_dispi.inf) in standard directories.");
                    return false;
                }

                foreach (var inf in infs)
                {
                    logCallback($"Injecting NVIDIA display driver: {inf}");
                    var psi = new ProcessStartInfo { 
                        FileName = "pnputil", 
                        Arguments = $"/add-driver \"{inf}\" /install", 
                        UseShellExecute = false, 
                        CreateNoWindow = true, 
                        RedirectStandardOutput = true 
                    };
                    
                    using var process = Process.Start(psi);
                    if (process == null) continue;
                    
                    await process.WaitForExitAsync(ct);
                    int exitCode = process.ExitCode;
                    
                    if (exitCode == 0 || exitCode == 3010) {
                         logCallback($"Successfully installed display driver natively! Exit code: {exitCode}");
                         return true;
                    }
                    if (exitCode == 259) {
                         logCallback($"INF {Path.GetFileName(inf)} skipped internally - hardware up to date or device signature mismatch (PNP 259).");
                         continue;
                    }
                    logCallback($"INF {Path.GetFileName(inf)} execution dropped strictly with exit code {exitCode}.");
                }
            }
            catch (OperationCanceledException)
            {
                logCallback("Installation loop forcefully aborted via cancellation token.");
            }
            catch (Exception ex)
            {
                logCallback($"PnpUtil native installation pipeline failed: {ex.Message}");
            }
            return false;
        }

        public async Task<int> UninstallSilentAsync(Action<string> logCallback, CancellationToken ct)
        {

            string sysWow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
            string runDll32 = Path.Combine(sysWow64, "RunDll32.exe");
            
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string nvi2Dll = Path.Combine(progFiles, "NVIDIA Corporation", "Installer2", "InstallerCore", "NVI2.DLL");
            
            string args = $"\"{nvi2Dll}\",UninstallPackage Display.Driver -silent -n";
            
            try
            {
                logCallback($"Launching NVIDIA uninstaller: {runDll32} {args}");
                
                var psi = new ProcessStartInfo
                {
                    FileName = runDll32,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    logCallback("Failed to start the NVIDIA uninstaller process.");
                    return -1;
                }

                await process.WaitForExitAsync(ct);
                
                int exitCode = process.ExitCode;
                logCallback($"NVIDIA uninstaller completed with exit code: {exitCode}");
                
                return exitCode;
            }
            catch (Exception ex)
            {
                logCallback($"Uninstallation failed: {ex.Message}");
                return -1;
            }
        }
    }
}
