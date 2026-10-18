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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace kaliteConfig.Services
{
    public sealed class NvidiaComponent
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public bool IsSelected { get; set; }
        public bool IsLocked { get; init; }
        public bool IsInstallerManaged { get; init; }
        public string? Requires { get; init; }
        public long ApproxSizeBytes { get; init; }

        public string SizeText => ApproxSizeBytes > 0
            ? $"{ApproxSizeBytes / (1024.0 * 1024):0} MB" : "";
    }

    public sealed record PackageVerificationInfo(
        string FileName, long SizeBytes, string Sha256,
        string SignatureSubject, bool SignatureValid, string SignatureError);

    public class NvidiaPackageService
    {
        private static readonly HttpClient _http = new();

        public static bool IsAllowedUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
                return false;

            string host = uri.Host.ToLowerInvariant();
            return host == "nvidia.com" || host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new InvalidOperationException(
                    $"Refusing download: URL is not an https://*.nvidia.com location ({url}).");

            string tempDir = Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia");
            Directory.CreateDirectory(tempDir);
            string fileName = Uri.EscapeDataString(Path.GetFileName(new Uri(url).LocalPath));
            string destPath = Path.Combine(tempDir, fileName);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);

            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            var sw = Stopwatch.StartNew();
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && sw.ElapsedMilliseconds > 120)
                {
                    progress?.Report(read * 100.0 / total);
                    sw.Restart();
                }
            }
            progress?.Report(100);
            return destPath;
        }

        #region WinVerifyTrust interop

        private const uint ERROR_SUCCESS = 0;
        private static readonly Guid WinTrustActionGenericV2 = new("{00AAC56B-CD44-11D0-8CC2-00C04FC295EE}");

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public nint hFile;
            public nint pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public nint pPolicyCallbackData;
            public nint pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public nint pFile;
            public uint dwStateAction;
            public nint hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = false)]
        private static extern uint WinVerifyTrust(nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        private static string? VerifyNvidiaSignature(string filePath, out string subject)
        {
            subject = "";
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = filePath,
                };
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,
                    pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()),
                    dwStateAction = 0,
                };
                try
                {
                    Marshal.StructureToPtr(fileInfo, data.pFile, false);
                    uint result = WinVerifyTrust(nint.Zero, WinTrustActionGenericV2, ref data);
                    if (result != ERROR_SUCCESS)
                        return $"WinVerifyTrust failed: 0x{result:X8}";
                }
                finally
                {
                    Marshal.FreeHGlobal(data.pFile);
                }

#pragma warning disable SYSLIB0057
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                subject = cert.Subject;
                if (!subject.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return $"Package signer is not NVIDIA: {subject}";
                return null;
            }
            catch (Exception ex)
            {
                return $"Signature verification error: {ex.Message}";
            }
        }

        #endregion

        public async Task<(PackageVerificationInfo? Info, string? Error)> VerifyAsync(string filePath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    if (!fi.Exists) return (null, "Downloaded package is missing.");

                    string sha256;
                    using (var stream = fi.OpenRead())
                        sha256 = Convert.ToHexString(SHA256.HashData(stream));

                    string? sigError = VerifyNvidiaSignature(filePath, out string subject);

                    var info = new PackageVerificationInfo(fi.Name, fi.Length, sha256, subject,
                        SignatureValid: sigError is null, SignatureError: sigError ?? "");
                    return sigError is null ? (info, null) : ((PackageVerificationInfo?)null, (string?)sigError);
                }
                catch (Exception ex)
                {
                    return ((PackageVerificationInfo?)null, (string?)$"Verification failed: {ex.Message}");
                }
            }, ct);
        }

        private const string Expected7zrSha256 = "AD4C82FADCBDF93C03B4FC440F300509C7D60C5C2F4D183E35D9D70D6957037D";

        private async Task<string> Ensure7zrAsync(CancellationToken ct)
        {
            string toolsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Tools");
            Directory.CreateDirectory(toolsDir);
            string path = Path.Combine(toolsDir, "7zr.exe");

            bool valid = false;
            if (File.Exists(path) && new FileInfo(path).Length > 100_000)
            {
                using var fs = File.OpenRead(path);
                valid = Convert.ToHexString(SHA256.HashData(fs)) == Expected7zrSha256;
            }
            if (!valid)
            {
                byte[] bytes = await _http.GetByteArrayAsync("https://www.7-zip.org/a/7zr.exe", ct);
                using var payload = new MemoryStream(bytes);
                string hash = Convert.ToHexString(SHA256.HashData(payload));
                if (hash != Expected7zrSha256)
                    throw new InvalidOperationException(
                        $"Downloaded 7zr.exe failed hash verification ({hash}). Refusing to execute it.");
                await File.WriteAllBytesAsync(path, bytes, ct);
            }
            return path;
        }

        public async Task<string> ExtractAsync(string packagePath, Action<string> log, CancellationToken ct)
        {
            string extractDir = Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia", "extract");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            string z7 = await Ensure7zrAsync(ct);
            log($"Extracting with {z7} …");

            var psi = new ProcessStartInfo
            {
                FileName = z7,
                Arguments = $"x \"{packagePath}\" -o\"{extractDir}\" -y -bso0 -bsp0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("7zr.exe failed to start.");
            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"7z extraction failed (exit {proc.ExitCode}): {output}");

            log($"Extracted to {extractDir}");
            return extractDir;
        }

        private static readonly Dictionary<string, (string Label, string Desc, bool DefaultOn)> KnownComponents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Display.Driver"]         = ("Display Driver", "The core graphics driver. Required - cannot be removed.", true),
            ["Display.ControlPanel"]   = ("NVIDIA Control Panel", "Per-display and 3D settings UI.", true),
            ["Display.PhysX"]          = ("PhysX", "Legacy physics engine used by some games.", true),
            ["HDAudio.Driver"]         = ("HD Audio Driver", "Audio over HDMI/DisplayPort. Needed for monitor/TV speakers.", true),
            ["MSVCRuntime2017"]        = ("Visual C++ 2017 Runtime", "Microsoft runtime required by the driver.", true),
            ["MSVCRuntime2019"]        = ("Visual C++ 2019+ Runtime", "Microsoft runtime required by the driver.", true),
            ["Display.NVWMI"]          = ("NVWMI Provider", "WMI instrumentation for monitoring tools.", true),
            ["NvContainer"]            = ("NVIDIA Container Services", "Background services host (drives telemetry, ShadowPlay, NvApp).", false),
            ["NvContainer.LocalSystem"] = ("Container Service (LocalSystem)", "System-level service instance.", false),
            ["NvContainer.Session"]    = ("Container Service (Session)", "Per-user service instance.", false),
            ["NvContainer.User"]       = ("Container Service (User)", "User service instance.", false),
            ["NvPlugin.Watchdog"]      = ("Plugin Watchdog", "NVIDIA App plugin watchdog.", false),
            ["Display.NvApp"]          = ("NVIDIA App", "New unified driver UI (replaces GeForce Experience).", false),
            ["Display.NvApp.MessageBus"] = ("NVIDIA App Message Bus", "Inter-process messaging for NVIDIA App.", false),
            ["Display.NvApp.NvBackend"] = ("NVIDIA App Back-end", "Background services for NVIDIA App.", false),
            ["Display.NvApp.NvCPL"]    = ("NVIDIA App Control Panel bridge", "Links NVIDIA App to Control Panel settings.", false),
            ["ShadowPlay"]             = ("ShadowPlay", "Game recording/instant replay (part of NVIDIA App).", false),
            ["NvTelemetry"]            = ("Telemetry", "Usage data collection sent to NVIDIA.", false),
            ["NvDLISR"]                = ("Driver Store Reload Service", "Dynamic driver-state reload service.", false),
            ["FrameViewSdk"]           = ("FrameView SDK", "Performance overlay/stat capture SDK.", false),
            ["VirtualAudio.Driver"]    = ("Virtual Audio Driver", "Audio capture device for recording features.", false),
        };

        public Task<List<NvidiaComponent>> ParseComponentsAsync(string extractDir, Action<string> log)
            => Task.Run(() =>
            {
                var components = new List<NvidiaComponent>();
                string cfgPath = Path.Combine(extractDir, "setup.cfg");
                if (!File.Exists(cfgPath))
                    throw new InvalidOperationException($"setup.cfg not found in {extractDir} - not a valid NVIDIA package layout.");

                var doc = XDocument.Load(cfgPath);
                var subPackages = doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("sub-package", StringComparison.OrdinalIgnoreCase) &&
                                (string?)e.Attribute("name") is not null)
                    .ToList();

                if (subPackages.Count == 0)
                    throw new InvalidOperationException("setup.cfg has no <sub-package> entries - unknown package schema.");

                foreach (var el in subPackages)
                {
                    string id = (string?)el.Attribute("name") ?? "";
                    if (id.Length == 0) continue;

                    string disposition = (string?)el.Attribute("disposition") ?? "";
                    bool userSelectable = ((string?)el.Attribute("userSelectable"))?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;
                    bool hidden = ((string?)el.Attribute("hidden")) is not null;
                    bool isCritical = disposition.Equals("critical", StringComparison.OrdinalIgnoreCase);

                    var known = KnownComponents.TryGetValue(id, out var meta)
                        ? meta
                        : (Label: id, Desc: "New in this package - not in the known list, so it is left selected.", DefaultOn: true);

                    string dir = ResolveComponentDir(extractDir, id);
                    long size = Directory.Exists(dir)
                        ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                   .Sum(f => new FileInfo(f).Length)
                        : 0;

                    components.Add(new NvidiaComponent
                    {
                        Id = id,
                        Name = known.Label,
                        Description = known.Desc +
                            (hidden ? " (hidden package - internal)" : "") +
                            (!userSelectable ? " (installer-managed)" : ""),
                        IsLocked = isCritical,
                        IsInstallerManaged = !userSelectable,
                        IsSelected = isCritical || (userSelectable && known.DefaultOn),
                        ApproxSizeBytes = size,
                    });
                }

                log($"Parsed {components.Count} sub-packages from setup.cfg.");
                return components;
            });

        internal static string ResolveComponentDir(string extractDir, string id)
        {
            string exact = Path.Combine(extractDir, id);
            if (Directory.Exists(exact)) return exact;

            string prefix = id.Split('.')[0];
            return prefix.Length == 0 ? exact : Path.Combine(extractDir, prefix);
        }

        public Task<string> ApplySelectionAsync(string extractDir, IReadOnlyList<NvidiaComponent> components, Action<string> log)
            => Task.Run(() =>
            {
                string cfgPath = Path.Combine(extractDir, "setup.cfg");
                var doc = XDocument.Load(cfgPath);

                int installerManaged = components.Count(c => c.IsInstallerManaged);
                var deselected = components
                    .Where(c => !c.IsSelected && !c.IsLocked && !c.IsInstallerManaged)
                    .Select(c => c.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var excluded = new List<string>();
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int strippedEntries = 0;

                foreach (var el in doc.Descendants()
                         .Where(e => e.Name.LocalName.Equals("sub-package", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                {
                    string id = (string?)el.Attribute("name") ?? "";
                    if (id.Length == 0) continue;

                    if (!deselected.Contains(id))
                    {
                        kept.Add(id);
                        continue;
                    }

                    el.SetAttributeValue("disposition", "demand");
                    el.SetAttributeValue("hidden", "true");
                    strippedEntries += StripPayloadReferences(extractDir, el, id);
                    excluded.Add(id);
                }

                var excludedSet = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
                int strippedEdges = 0;
                foreach (var edge in doc.Descendants()
                         .Where(e => e.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                {
                    string? type = (string?)edge.Attribute("type");
                    string? target = (string?)edge.Attribute("package");
                    if (target is null || !excludedSet.Contains(target)) continue;
                    if (type is null ||
                        !(type.Equals("installs", StringComparison.OrdinalIgnoreCase) ||
                          type.Equals("requires", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    edge.Remove();
                    strippedEdges++;
                }

                doc.Save(cfgPath);

                log(excluded.Count == 0
                    ? "setup.cfg: nothing to exclude (every component was selected)."
                    : $"setup.cfg updated: {excluded.Count} component(s) excluded ({strippedEntries} payload entr{(strippedEntries == 1 ? "y" : "ies")}, {strippedEdges} dependency edge{(strippedEdges == 1 ? "" : "s")} removed): {string.Join(", ", excluded)}");
                if (installerManaged > 0)
                    log($"{installerManaged} installer-managed component(s) left as NVIDIA ships them.");

                int removedDirs = StripExcludedPayload(extractDir, excluded, kept, log);
                if (removedDirs > 0)
                    log($"Removed {removedDirs} excluded component folder(s) from the package.");

                return cfgPath;
            });

        private static int StripPayloadReferences(string extractDir, XElement subPackage, string componentId)
        {
            int removed = 0;
            foreach (var child in subPackage.Elements().ToList())
            {
                bool referencesOwnPayload = child.Attributes()
                    .Any(a => ReferencesComponentPayload(extractDir, componentId, a.Value));
                if (!referencesOwnPayload) continue;
                child.Remove();
                removed++;
            }
            return removed;
        }

        private static bool ReferencesComponentPayload(string extractDir, string componentId, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string relative = value.Trim().Replace('/', '\\').TrimStart('\\');
            int separator = relative.IndexOf('\\');
            string folder = separator < 0 ? relative : relative[..separator];
            if (folder.Length == 0) return false;

            bool belongs = folder.Equals(componentId, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(componentId + ".", StringComparison.OrdinalIgnoreCase)
                || componentId.StartsWith(folder + ".", StringComparison.OrdinalIgnoreCase)
                || componentId.Split('.')[0].Equals(folder, StringComparison.OrdinalIgnoreCase);
            if (!belongs) return false;

            try
            {
                return Directory.Exists(Path.Combine(extractDir, folder))
                    || File.Exists(Path.Combine(extractDir, folder));
            }
            catch
            {
                return false;
            }
        }

        private static int StripExcludedPayload(
            string extractDir, IReadOnlyList<string> excluded, IReadOnlyCollection<string> kept, Action<string> log)
        {
            if (excluded.Count == 0) return 0;

            string root = Path.GetFullPath(extractDir);
            var keptFolders = new HashSet<string>(
                kept.Select(id => Path.GetFullPath(ResolveComponentDir(root, id))), StringComparer.OrdinalIgnoreCase);

            int removed = 0;
            foreach (string id in excluded)
            {
                string full = Path.GetFullPath(ResolveComponentDir(root, id));
                if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (keptFolders.Contains(full)) continue;
                if (!Directory.Exists(full)) continue;

                try
                {
                    Directory.Delete(full, recursive: true);
                    removed++;
                    log($"Removed excluded component folder: {Path.GetFileName(full)}");
                }
                catch (Exception ex)
                {
                    log($"Could not remove {Path.GetFileName(full)}: {ex.Message}");
                }
            }
            return removed;
        }

        public async Task<(int ExitCode, bool RebootRequired, string LogTail)> InstallAsync(
            string extractDir, bool cleanInstall, Action<string> log, CancellationToken ct)
        {
            string setupExe = Path.Combine(extractDir, "setup.exe");
            if (!File.Exists(setupExe))
                throw new InvalidOperationException($"setup.exe not found in {extractDir}.");

            var args = new StringBuilder("-s -noreboot -noeula");
            if (cleanInstall) args.Append(" -clean");

            log($"Launching (elevated): setup.exe {args}");
            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = args.ToString(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("setup.exe failed to start.");
            log("Installer is running (elevated). Screen may flicker - this is normal.");

            string tail = await TailInstallerLogAsync(proc, extractDir, log, ct);

            await proc.WaitForExitAsync(ct);
            int exit = proc.ExitCode;
            bool reboot = exit is 1641 or 3010;
            log($"Installer exit code: {exit}{(reboot ? " (reboot required)" : exit == 0 ? " (success)" : " (failed)")}");
            return (exit, reboot, tail);
        }

        private static async Task<string> TailInstallerLogAsync(Process proc, string extractDir, Action<string> log, CancellationToken ct)
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NVIDIA Corporation", "NVSMI");
            string? logFile = null;
            var sw = Stopwatch.StartNew();
            long lastLen = 0;

            while (!proc.HasExited || sw.ElapsedMilliseconds < 3000)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (logFile is null && Directory.Exists(logDir))
                        logFile = Directory.GetFiles(logDir, "*.log", SearchOption.TopDirectoryOnly)
                                           .Select(f => new FileInfo(f))
                                           .OrderByDescending(f => f.LastWriteTime)
                                           .FirstOrDefault()?.FullName;

                    if (logFile is not null)
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Length > lastLen)
                        {
                            fs.Seek(lastLen, SeekOrigin.Begin);
                            using var sr = new StreamReader(fs);
                            string chunk = await sr.ReadToEndAsync(ct);
                            lastLen = fs.Length;
                            foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                if (!string.IsNullOrWhiteSpace(line)) log(line.TrimEnd());
                        }
                    }
                }
                catch {  }

                await Task.Delay(700, ct);
                if (proc.HasExited) break;
            }
            return logFile ?? "";
        }

        public static void CleanupTemp(bool keepForDebug)
        {
            string root = Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia");
            try
            {
                if (keepForDebug)
                {
                    return;
                }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception) { }
        }

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia");
    }
}
