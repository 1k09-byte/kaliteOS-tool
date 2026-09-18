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
    /// <summary>One installable component parsed from setup.cfg.</summary>
    public sealed class NvidiaComponent
    {
        public required string Id { get; init; }            // e.g. "Display.Driver", "HDAudio", "PhysX"
        public required string Name { get; init; }          // human label
        public string Description { get; init; } = "";
        public bool IsSelected { get; set; }
        public bool IsLocked { get; init; }                 // display driver: non-removable
        public string? Requires { get; init; }              // hard dependency (component Id)
        public long ApproxSizeBytes { get; init; }

        public string SizeText => ApproxSizeBytes > 0
            ? $"{ApproxSizeBytes / (1024.0 * 1024):0} MB" : "";
    }

    /// <summary>Verification/extract metadata shown in the UI's details expander.</summary>
    public sealed record PackageVerificationInfo(
        string FileName, long SizeBytes, string Sha256,
        string SignatureSubject, bool SignatureValid, string SignatureError);

    /// <summary>
    /// Parts 2–6: download (host-enforced) → verify (WinVerifyTrust + SHA-256) →
    /// extract (7z SFX) → parse setup.cfg → rewrite for component selection →
    /// run setup.exe elevated with silent switches.
    ///
    /// SAFETY INVARIANTS (do not weaken):
    ///  1. Download URLs must be https://*.nvidia.com — enforced before any byte.
    ///  2. The package must carry a valid NVIDIA Authenticode signature, verified
    ///     with WinVerifyTrust, BEFORE extraction or execution. Failure stops
    ///     everything; driver-signature enforcement is never bypassed.
    ///  3. Elevation is a real UAC prompt via UseShellExecute=true + runas verb.
    ///  4. The base display driver component can never be deselected.
    /// </summary>
    public class NvidiaPackageService
    {
        private static readonly HttpClient _http = new();

        // ------------------------------------------------------------------
        // Part 2 — download with host enforcement
        // ------------------------------------------------------------------

        public static bool IsAllowedUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
                return false;

            string host = uri.Host.ToLowerInvariant();
            return host == "nvidia.com" || host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Download with per-chunk progress. Throws on disallowed host.</summary>
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

        // ------------------------------------------------------------------
        // Part 2 — signature + hash verification
        // ------------------------------------------------------------------

        #region WinVerifyTrust interop

        private const uint ERROR_SUCCESS = 0;
        private static readonly Guid DriverActionId = new("{F750E6C3-38EE-11D1-85E5-00C04FC295EE}"); // driver packages
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
            public uint dwUIChoice;          // 2 = WTD_UI_NONE
            public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public nint pFile;
            public uint dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
            public nint hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;         // 0 = WTD_UICONTEXT_EXECUTE
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = false)]
        private static extern uint WinVerifyTrust(nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>
        /// Verifies the file's Authenticode signature via WinVerifyTrust and
        /// requires the signing subject to contain "NVIDIA". Returns null on
        /// success, or an error description on any failure.
        /// </summary>
        private static unsafe string? VerifyNvidiaSignature(string filePath, out string subject)
        {
            subject = "";
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                    pcwszFilePath = filePath,
                };
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)sizeof(WINTRUST_DATA),
                    dwUIChoice = 2,               // no UI
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,            // file
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

                // Certificate subject check via X509 (the file is countersigned
                // by Microsoft's cross-sign; the leaf publisher is NVIDIA).
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
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

        /// <summary>
        /// Full Part 2 gate: compute SHA-256 + size, verify the NVIDIA
        /// Authenticode signature. Returns null (with info) when valid, or
        /// (null info + error) when the package must not be touched.
        /// </summary>
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

        // ------------------------------------------------------------------
        // Part 3 — 7z SFX extraction
        // ------------------------------------------------------------------

        /// <summary>
        /// Pinned SHA-256 of the official https://www.7-zip.org/a/7zr.exe
        /// (verified stable across downloads). 7zr.exe is NOT Authenticode-signed
        /// (verified: Get-AuthenticodeSignature reports NotSigned), so a signature
        /// check would always fail — the pinned hash IS the trust anchor here.
        /// If 7-zip.org ships a new build, this hash must be updated deliberately.
        /// </summary>
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

        // ------------------------------------------------------------------
        // Part 3/4 — setup.cfg parsing into components
        // ------------------------------------------------------------------

        /// <summary>
        /// NVIDIA sub-package names (setup.cfg &lt;sub-package name="…"&gt;, verified
        /// against the real 616.92 package) → human labels. Anything unmapped
        /// still appears with its raw name so nothing is silently hidden.
        /// </summary>
        private static readonly Dictionary<string, (string Label, string Desc, bool DefaultOn)> KnownComponents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Display.Driver"]         = ("Display Driver", "The core graphics driver. Required — cannot be removed.", true),
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

        /// <summary>
        /// Parses &lt;extract&gt;/setup.cfg into a component checklist. Schema
        /// verified against a real 616.92 package: components are &lt;sub-package
        /// name="…" disposition="…" [userSelectable="false"] [hidden="…"]&gt;
        /// elements. "critical" = the display driver (locked ON). Everything
        /// else defaults to the KnownComponents map (telemetry/NvApp/bloat
        /// off, driver/audio/PhysX/runtimes on).
        /// </summary>
        public Task<List<NvidiaComponent>> ParseComponentsAsync(string extractDir, Action<string> log)
            => Task.Run(() =>
            {
                var components = new List<NvidiaComponent>();
                string cfgPath = Path.Combine(extractDir, "setup.cfg");
                if (!File.Exists(cfgPath))
                    throw new InvalidOperationException($"setup.cfg not found in {extractDir} — not a valid NVIDIA package layout.");

                var doc = XDocument.Load(cfgPath);
                var subPackages = doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("sub-package", StringComparison.OrdinalIgnoreCase) &&
                                (string?)e.Attribute("name") is not null)
                    .ToList();

                if (subPackages.Count == 0)
                    throw new InvalidOperationException("setup.cfg has no <sub-package> entries — unknown package schema.");

                foreach (var el in subPackages)
                {
                    string id = (string?)el.Attribute("name") ?? "";
                    if (id.Length == 0) continue;

                    string disposition = (string?)el.Attribute("disposition") ?? "";
                    bool userSelectable = ((string?)el.Attribute("userSelectable"))?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;
                    bool hidden = ((string?)el.Attribute("hidden")) is not null;
                    bool isCritical = disposition.Equals("critical", StringComparison.OrdinalIgnoreCase);

                    var known = KnownComponents.TryGetValue(id, out var meta)
                        ? meta : (Label: id, Desc: "", DefaultOn: false);

                    // Sizes: the package lays out each sub-package's payload in a
                    // matching folder (Display.Driver/…). NvContainer* entries
                    // share one folder — try exact, then prefix match.
                    long size = 0;
                    string dir = Path.Combine(extractDir, id);
                    if (!Directory.Exists(dir))
                    {
                        string prefix = id.Split('.')[0];
                        string candidate = Path.Combine(extractDir, prefix);
                        if (Directory.Exists(candidate)) dir = candidate;
                    }
                    if (Directory.Exists(dir))
                        size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                         .Sum(f => new FileInfo(f).Length);

                    components.Add(new NvidiaComponent
                    {
                        Id = id,
                        Name = known.Label,
                        Description = known.Desc +
                            (hidden ? " (hidden package — internal)" : "") +
                            (!userSelectable ? " (installer-managed)" : ""),
                        // Locked = the display driver itself. Installer-managed
                        // internals (userSelectable=false) also can't be toggled,
                        // but still show so nothing installs invisibly.
                        IsLocked = isCritical,
                        IsSelected = isCritical || (userSelectable && known.DefaultOn),
                        ApproxSizeBytes = size,
                    });
                }

                log($"Parsed {components.Count} sub-packages from setup.cfg.");
                return components;
            });

        // ------------------------------------------------------------------
        // Part 5 — setup.cfg rewrite + elevated silent install
        // ------------------------------------------------------------------

        /// <summary>
        /// Rewrites setup.cfg so deselected sub-packages are excluded. VERIFIED
        /// against a real 616.92 package: installable units are &lt;sub-package
        /// name="…"&gt; elements; setting disposition="hidden" (the attribute the
        /// schema itself uses, e.g. VirtualAudio.Driver hidden="true") removes
        /// them from the install set without deleting the structure.
        /// </summary>
        public Task<string> ApplySelectionAsync(string extractDir, IReadOnlyList<NvidiaComponent> components, Action<string> log)
            => Task.Run(() =>
            {
                string cfgPath = Path.Combine(extractDir, "setup.cfg");
                var doc = XDocument.Load(cfgPath);

                var deselected = components
                    .Where(c => !c.IsSelected && !c.IsLocked)
                    .Select(c => c.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                int hidden = 0;
                foreach (var el in doc.Descendants()
                         .Where(e => e.Name.LocalName.Equals("sub-package", StringComparison.OrdinalIgnoreCase)))
                {
                    string id = (string?)el.Attribute("name") ?? "";
                    if (deselected.Contains(id))
                    {
                        el.SetAttributeValue("disposition", "hidden");
                        hidden++;
                    }
                }

                doc.Save(cfgPath);
                log($"setup.cfg updated: {hidden} component(s) excluded.");
                return cfgPath;
            });

        /// <summary>
        /// Runs the extracted setup.exe elevated with NVIDIA's documented silent
        /// switches. Streams the installer's own log tail into the UI. Returns
        /// the exit code — 0 success, 1 failure, 1641/3010 reboot required.
        /// </summary>
        public async Task<(int ExitCode, bool RebootRequired, string LogTail)> InstallAsync(
            string extractDir, bool cleanInstall, Action<string> log, CancellationToken ct)
        {
            string setupExe = Path.Combine(extractDir, "setup.exe");
            if (!File.Exists(setupExe))
                throw new InvalidOperationException($"setup.exe not found in {extractDir}.");

            // ACCURACY FLAG: -s (silent), -noreboot, -noeula and -clean are the
            // switches the NVIDIA installer has supported for years and are the
            // ones NVCleanstall/NVSlimmer drive. Run "setup.exe -?" / -h against
            // the real extracted package before relying on the exact set —
            // the exact grammar is not contractually documented.
            var args = new StringBuilder("-s -noreboot -noeula");
            if (cleanInstall) args.Append(" -clean");

            log($"Launching (elevated): setup.exe {args}");
            var psi = new ProcessStartInfo
            {
                FileName = setupExe,
                Arguments = args.ToString(),
                UseShellExecute = true,           // required for the UAC prompt
                Verb = "runas",                   // real UAC consent — no tricks
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("setup.exe failed to start.");
            log("Installer is running (elevated). Screen may flicker — this is normal.");

            // Progress: poll the installer's own log file (NVIDIA writes
            // %ProgramData%\NVIDIA Corporation\... or the extraction dir).
            string tail = await TailInstallerLogAsync(proc, extractDir, log, ct);

            await proc.WaitForExitAsync(ct);
            int exit = proc.ExitCode;
            bool reboot = exit is 1641 or 3010;
            log($"Installer exit code: {exit}{(reboot ? " (reboot required)" : exit == 0 ? " (success)" : " (failed)")}");
            return (exit, reboot, tail);
        }

        private static async Task<string> TailInstallerLogAsync(Process proc, string extractDir, Action<string> log, CancellationToken ct)
        {
            // The NVIDIA installer writes its progress to a log under
            // %ProgramData%\NVIDIA Corporation\NVSMI\ or similar. Poll the
            // newest matching file and surface new lines.
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
                catch { /* log polling is best-effort */ }

                await Task.Delay(700, ct);
                if (proc.HasExited) break;
            }
            return logFile ?? "";
        }

        /// <summary>Deletes the temp extraction directory after a successful install.</summary>
        public static void CleanupTemp(bool keepForDebug)
        {
            string root = Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia");
            try
            {
                if (keepForDebug)
                {
                    Debug.WriteLine($"Keeping temp dir for debugging: {root}");
                    return;
                }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex) { Debug.WriteLine($"CleanupTemp: {ex.Message}"); }
        }

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "kaliteConfig", "nvidia");
    }
}
