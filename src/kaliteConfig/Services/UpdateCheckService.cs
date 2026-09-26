using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>
/// Consumer auto-update: checks GitHub Releases on the project repo and
/// downloads the newest consumer setup exe.
///
/// - Repo constants live here; change them once when the repo moves.
/// - Uses /releases/latest, which GitHub only returns for full (non-prerelease,
///   non-draft) releases - so tagging an alpha never ships to consumers.
/// - Asset lookup by name prefix "kaliteConfig-Consumer-Setup-" (what
///   Installer/Build-Consumer.ps1 produces via kaliteConfig-Consumer.iss).
/// - Every network call is best-effort: a timeout/404/rate-limit never
///   blocks startup or surfaces an error; the app just runs as-is.
/// - Compiled only in the CONSUMER flavor (see csproj ConsumerBuild group).
/// </summary>
#if CONSUMER
public sealed class UpdateCheckService
#else
public sealed class UpdateCheckService // full flavor: type exists but is unused
#endif
{
    public const string Owner = "1k09-byte";
    public const string RepoName = "kaliteOS-tool";
    public const string ReleasesApiUrl = $"https://api.github.com/repos/{Owner}/{RepoName}/releases/latest";
    // Single flavor now: releases carry the FULL installer. Accept both
    // names so older releases remain updatable-to.
    private const string AssetNamePrefix = "kaliteConfig-Setup-";
    private const string LegacyAssetNamePrefix = "kaliteConfig-Consumer-Setup-";
    private const string InnoUninstallRegKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{422E40DD-B1A5-4157-907F-690182582441}_is1";
    private const string KaliteOsInstallPathRegKey = @"SOFTWARE\KaliteOS";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("kaliteConfig-Consumer-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Latest full release info relevant to the consumer app; null when none/newer/failed.</summary>
    public sealed record LatestRelease(string Version, string Notes, string DownloadUrl, long SizeBytes);

    /// <summary>One downloadable setup asset inside a release.</summary>
    internal sealed record SetupAsset(string Name, string DownloadUrl, long SizeBytes);

    /// <summary>Best-effort file log for update diagnostics (the UI stays silent by design).</summary>
    internal static void LogDiag(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "updater.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    private static string PendingMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "update-pending.json");

    /// <summary>
    /// Records the version an update was just launched for. If the app later
    /// still offers that same version, the install didn't take (different
    /// install folder, dev copy, side-by-side flavor) - and the UI can say so
    /// instead of looping silently.
    /// </summary>
    internal static void ClearPendingUpdate()
    {
        try { if (File.Exists(PendingMarkerPath)) File.Delete(PendingMarkerPath); }
        catch { }
    }

    /// <summary>
    /// Directory where this product is registered (Inno uninstall key or
    /// KaliteOS InstallPath). Used to target silent upgrades at the real
    /// install folder instead of accidentally side-by-side with a dev build.
    /// </summary>
    internal static string? GetRegisteredInstallDirectory()
    {
        try
        {
            using var kalite = Registry.LocalMachine.OpenSubKey(KaliteOsInstallPathRegKey, writable: false);
            if (kalite?.GetValue("InstallPath") is string fromKalite && !string.IsNullOrWhiteSpace(fromKalite))
            {
                var trimmed = fromKalite.Trim().TrimEnd('\\', '/');
                if (Directory.Exists(trimmed)) return trimmed;
            }
        }
        catch { }

        try
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(InnoUninstallRegKey, writable: false);
            if (uninstall?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
            {
                var trimmed = loc.Trim().TrimEnd('\\', '/');
                if (Directory.Exists(trimmed)) return trimmed;
            }
        }
        catch { }

        return null;
    }

    internal static string? GetInstalledExePath()
    {
        var dir = GetRegisteredInstallDirectory();
        if (dir is null) return null;
        var exe = Path.Combine(dir, "kaliteConfig.exe");
        return File.Exists(exe) ? exe : null;
    }

    internal static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()),
                Path.GetFullPath(b.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static string? TryReadExeVersion(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            return $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
        }
        catch { return null; }
    }

    /// <summary>
    /// True when a registered install on disk already has the pending release
    /// but this process is still an older build from another path (the loop in
    /// the update dialog screenshot).
    /// </summary>
    internal static bool ShouldHandOffToInstalledCopy(
        string? pendingVersion,
        string? runningVersion,
        string? installedExeVersion,
        string? runningExePath,
        string? installedExePath)
    {
        if (string.IsNullOrWhiteSpace(pendingVersion)
            || string.IsNullOrWhiteSpace(runningVersion)
            || string.IsNullOrWhiteSpace(installedExeVersion)
            || string.IsNullOrWhiteSpace(runningExePath)
            || string.IsNullOrWhiteSpace(installedExePath))
        {
            return false;
        }

        if (PathsEqual(runningExePath, installedExePath))
            return false;

        if (!IsNewer(pendingVersion, runningVersion))
            return false;

        // Install finished on disk (matches pending) while this session is stale.
        if (string.Equals(pendingVersion, installedExeVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        // Pending marker matches offer; installed copy is still ahead of us.
        return !IsNewer(pendingVersion, installedExeVersion)
               && IsNewer(installedExeVersion, runningVersion);
    }

    /// <summary>
    /// Starts the registered install and exits this process. Returns false when
    /// no hand-off was attempted.
    /// </summary>
    internal static bool TryLaunchInstalledCopyAndExit()
    {
        var installedExe = GetInstalledExePath();
        if (installedExe is null) return false;

        var runningPath = Environment.ProcessPath;
        if (PathsEqual(runningPath, installedExe))
            return false;

        try
        {
            bool isAdmin;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            var psi = new ProcessStartInfo
            {
                FileName = installedExe,
                UseShellExecute = true,
            };
            if (!isAdmin) psi.Verb = "runas";

            LogDiag($"handoff: launching installed copy {installedExe} (was {runningPath ?? "?"})");
            Process.Start(psi);
            ClearPendingUpdate();
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            LogDiag($"handoff: failed - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// After a silent upgrade, the user may still open a dev copy or an old
    /// shortcut. Switch once when the registered install already has pending.
    /// </summary>
    internal static bool TryLaunchInstalledCopyAndExitIfStaleSession()
    {
        var pending = ReadPendingUpdateVersion();
        var running = CurrentVersion;
        var installedExe = GetInstalledExePath();
        if (pending is null || running is null || installedExe is null) return false;

        var installedVer = TryReadExeVersion(installedExe);
        if (!ShouldHandOffToInstalledCopy(
                pending, running, installedVer, Environment.ProcessPath, installedExe))
        {
            return false;
        }

        return TryLaunchInstalledCopyAndExit();
    }

    /// <summary>
    /// Inno Setup silent flags for the in-app updater hand-off.
    ///
    /// - /NOCLOSEAPPLICATIONS: the app minimizes to the tray and swallows
    ///   WM_CLOSE, so Restart Manager can never close it; Setup then reported
    ///   "Some applications could not be shut down" and - because
    ///   /SUPPRESSMSGBOXES defaults to Abort - rolled the ENTIRE upgrade back
    ///   without a word. The app exits itself and Setup's [Code] kills
    ///   stragglers instead. (The old /CLOSEAPPLICATIONS=0 was not a real
    ///   switch: unknown parameters are ignored, so it disabled nothing.)
    /// - /LOG="…": a silent failure leaves no other trace on the machine.
    ///   That log is what lets the app explain the failure instead of just
    ///   re-offering the update forever.
    /// </summary>
    internal static string BuildSilentInstallerArguments(string? installDirectory, string? logPath = null)
    {
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS";
        if (!string.IsNullOrWhiteSpace(logPath))
            args += $" /LOG=\"{logPath.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            var dir = installDirectory.Trim().TrimEnd('\\');
            args += $" /DIR=\"{dir}\"";
        }
        return args;
    }

    internal static void WritePendingUpdate(string version)
    {
        try
        {
            var dir = Path.GetDirectoryName(PendingMarkerPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(PendingMarkerPath,
                JsonSerializer.Serialize(new { version, at = DateTime.Now }));
        }
        catch { }
    }

    /// <summary>Version recorded by <see cref="WritePendingUpdate"/>, or null when none.</summary>
    internal static string? ReadPendingUpdateVersion() => ReadPendingUpdate()?.Version;

    /// <summary>
    /// What the last update attempt left behind: which version we handed to
    /// Setup, where the setup exe and its log are, and whether the user has
    /// already been told that this attempt did not take.
    /// </summary>
    internal sealed record PendingUpdate(
        string Version,
        string? InstallerPath,
        string? LogPath,
        bool Reported);

    /// <summary>Inno Setup log path for a version - the same path we pass as /LOG.</summary>
    internal static string SetupLogPath(string version) => Path.Combine(
        Path.GetTempPath(), $"kaliteConfig-setup-{version.Trim()}.log");

    /// <summary>Where the downloaded setup exe for a version lives.</summary>
    internal static string SetupDownloadPath(string version) => Path.Combine(
        Path.GetTempPath(), $"{AssetNamePrefix}{version.Trim()}.exe");

    /// <summary>Result of the last update attempt, judged from the versions actually on disk.</summary>
    internal enum UpdateAttemptState
    {
        /// <summary>No attempt on record (or it was reconciled and cleared).</summary>
        None,
        /// <summary>This session runs the attempted version or newer - the install took.</summary>
        Applied,
        /// <summary>The registered install has the attempted version; this session is a stale copy.</summary>
        AppliedElsewhere,
        /// <summary>Setup ran (or never started) and the version did not change - say why.</summary>
        Failed,
    }

    /// <summary>
    /// Judges a pending attempt from the versions on disk. Pure - every
    /// input is supplied by the caller, so this is checked offline in
    /// tools/UpdateVerify. Never trust the fact that we once started a setup
    /// exe: the only proof an upgrade landed is the version of the file that
    /// is installed now.
    /// </summary>
    internal static UpdateAttemptState ClassifyPendingAttempt(
        PendingUpdate? pending,
        string? runningVersion,
        string? installedExeVersion)
    {
        if (pending is null || string.IsNullOrWhiteSpace(pending.Version))
            return UpdateAttemptState.None;

        // Install took: we are the version we asked for (or newer).
        if (!string.IsNullOrWhiteSpace(runningVersion) && !IsNewer(pending.Version, runningVersion))
            return UpdateAttemptState.Applied;

        // Install took on disk, but this process is still an older copy.
        if (!string.IsNullOrWhiteSpace(installedExeVersion)
            && !IsNewer(pending.Version, installedExeVersion))
            return UpdateAttemptState.AppliedElsewhere;

        return UpdateAttemptState.Failed;
    }

    /// <summary>
    /// Records an attempt. Called BEFORE the setup exe starts, because the
    /// setup kills this process: nothing after that point can be written.
    /// </summary>
    internal static void WritePendingUpdate(
        string version,
        string? installerPath = null,
        string? logPath = null,
        bool reported = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(PendingMarkerPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(PendingMarkerPath,
                JsonSerializer.Serialize(new
                {
                    version,
                    at = DateTime.Now,
                    installer = installerPath,
                    log = logPath,
                    reported,
                }));
        }
        catch { }
    }

    /// <summary>Full pending record, or null when no attempt is on record / unreadable.</summary>
    internal static PendingUpdate? ReadPendingUpdate()
    {
        try
        {
            if (!File.Exists(PendingMarkerPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(PendingMarkerPath));
            var root = doc.RootElement;
            var version = StringProperty(root, "version");
            if (string.IsNullOrWhiteSpace(version)) return null;
            return new PendingUpdate(
                version.Trim(),
                StringProperty(root, "installer"),
                StringProperty(root, "log"),
                root.TryGetProperty("reported", out var reported)
                    && reported.ValueKind == JsonValueKind.True);
        }
        catch { return null; }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Marks the attempt as already explained, so one failed upgrade is
    /// reported exactly once per version instead of re-prompting on every
    /// launch (the "it keeps offering the same update" loop).
    /// </summary>
    internal static void MarkPendingUpdateReported()
    {
        var pending = ReadPendingUpdate();
        if (pending is null) return;
        WritePendingUpdate(pending.Version, pending.InstallerPath, pending.LogPath, reported: true);
    }

    /// <summary>
    /// Reads an Inno Setup log and turns it into one line a user can act on.
    /// Never throws - diagnostics must not break the updater.
    /// </summary>
    internal static string? ReadInstallerLogSummary(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return null;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new System.Collections.Generic.List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return SummarizeInstallerLog(lines);
        }
        catch { return null; }
    }

    /// <summary>
    /// The reason a silent install aborted. Under /SUPPRESSMSGBOXES Setup
    /// defaults to Abort for the dialog it cannot show, so the log is the ONLY
    /// place the failure survives - without it the app can only say "the
    /// install didn't take". Pure: the caller reads the file.
    /// </summary>
    internal static string? SummarizeInstallerLog(System.Collections.Generic.IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) return null;

        const string AbortMarker = "Defaulting to Abort for suppressed message box";
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf(AbortMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var message = new System.Collections.Generic.List<string>();
            for (int j = i + 1; j < lines.Count; j++)
            {
                var text = StripLogPrefix(lines[j]);
                if (text.Length == 0) break;
                message.Add(text);
            }
            return message.Count > 0 ? string.Join(" ", message) : "Setup aborted.";
        }

        foreach (var (marker, summary) in LogFailureMarkers)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return summary;
            }
        }

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].IndexOf("Rolling back changes", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Setup rolled the installation back.";
        }

        return null;
    }

    /// <summary>Setup's own words for the failures we can name precisely.</summary>
    private static readonly (string Marker, string Summary)[] LogFailureMarkers =
    {
        ("Some applications could not be shut down",
            "Setup could not close the running kaliteConfig copy, so it aborted before replacing any file."),
        ("Setup files are corrupted",
            "The downloaded installer was corrupt - retry the download."),
        ("is not a valid Win32 application",
            "The downloaded installer was not a valid program - retry the download."),
        ("Access is denied",
            "Setup was denied access to the install folder (permissions or antivirus)."),
        ("The process cannot access the file",
            "An installed file was locked by another process while Setup replaced it."),
    };

    /// <summary>Strips Inno's "2026-09-16 18:39:15.205   " prefix (continuation lines carry none).</summary>
    private static string StripLogPrefix(string line)
    {
        var text = line.Trim();
        if (text.Length > 23
            && text[4] == '-' && text[7] == '-' && text[10] == ' '
            && text[13] == ':' && text[16] == ':')
        {
            text = text[23..].Trim();
        }
        return text;
    }

    /// <summary>Running app version ("0.1.0" style), or null when unreadable.</summary>
    public static string? CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                // All four parts: the release tags carry a revision
                // (v0.3.0.1), and truncating it made 0.3.0.1 compare as
                // 0.3.0 - the updater then offered the identical version.
                return v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch { return null; }
        }
    }

    /// <summary>Compares dotted numeric versions; true when remote > current. Non-numeric parts fail safe to false.</summary>
    public static bool IsNewer(string? remoteTag, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteTag) || string.IsNullOrWhiteSpace(currentVersion))
            return false;

        string remote = remoteTag.TrimStart('v', 'V');
        int[] r = remote.Split('.').Select(s => int.TryParse(s, out int n) ? n : -1).ToArray();
        int[] c = currentVersion.Split('.').Select(s => int.TryParse(s, out int n) ? n : -1).ToArray();
        if (r.Any(n => n < 0) || c.Any(n => n < 0) || r.Length == 0 || c.Length == 0)
            return false;

        int len = Math.Max(r.Length, c.Length);
        for (int i = 0; i < len; i++)
        {
            int rv = i < r.Length ? r[i] : 0;
            int cv = i < c.Length ? c[i] : 0;
            if (rv != cv) return rv > cv;
        }
        return false;
    }

    /// <summary>
    /// Queries the latest release. Returns null when: network failed, no full
    /// release exists yet, the release is not newer than the running build, or
    /// the release has no setup asset built for ITS OWN version.
    ///
    /// That last rule is the update-loop guard: installing any prefix-matching
    /// but stale asset (e.g. an older setup re-uploaded under a new tag)
    /// "succeeds" without changing the installed version, so the popup
    /// reappears forever. A version mismatch now means no offer, plus a log line.
    /// </summary>
    public async Task<LatestRelease?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogDiag($"check: HTTP {(int)response.StatusCode} - no offer");
                return null; // 404 = no releases yet; 403 = rate limit
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? (b.GetString() ?? "") : "";

            if (!IsNewer(tag, CurrentVersion))
            {
                LogDiag($"check: tag '{tag}' not newer than running {CurrentVersion ?? "?"} - no offer");
                return null;
            }

            SetupAsset? picked = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                picked = PickSetupAsset(assets, tag);

            if (picked is null)
            {
                LogDiag($"check: tag '{tag}' has no setup asset matching its own version - no offer (not installing a stale asset)");
                return null;
            }

            LogDiag($"check: offering {picked.Name} ({picked.SizeBytes} bytes) over running {CurrentVersion}");
            return new LatestRelease(tag.TrimStart('v', 'V'), notes, picked.DownloadUrl, picked.SizeBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the release's setup asset for <paramref name="tag"/>: candidates
    /// carry either setup prefix and end in .exe, but only one whose filename
    /// contains the release's own version is eligible. The full-flavor prefix
    /// wins over the legacy consumer prefix when both match (single-flavor
    /// going forward; old consumer installs migrate to it).
    /// Pure logic - unit-tested in tools/UpdateVerify without network.
    /// </summary>
    internal static SetupAsset? PickSetupAsset(JsonElement assetsArray, string tag)
    {
        string version = tag.TrimStart('v', 'V').Trim();
        if (version.Length == 0) return null;

        var matching = new System.Collections.Generic.List<SetupAsset>();
        foreach (var asset in assetsArray.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";
            if ((!name.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase)
                 && !name.StartsWith(LegacyAssetNamePrefix, StringComparison.OrdinalIgnoreCase))
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.Contains(version, StringComparison.OrdinalIgnoreCase))
                continue; // not built for this release - never install it as "the update"

            string? url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
            matching.Add(new SetupAsset(name, url!, size));
        }

        if (matching.Count == 0) return null;
        return matching.FirstOrDefault(a => a.Name.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase))
            ?? matching[0];
    }

    /// <summary>Downloads the release asset to %TEMP%. Returns the local path.</summary>
    public async Task<string> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string tempPath = SetupDownloadPath(release.Version);
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

        // Long timeout for the download itself; the 10s client timeout applies
        // to the check call, so use a fresh client here.
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("kaliteConfig-Consumer-Updater/1.0");

        using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = release.SizeBytes > 0
            ? release.SizeBytes
            : response.Content.Headers.ContentLength ?? -1;

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

        var buffer = new byte[1 << 16];
        long read = 0;
        var lastReport = DateTime.UtcNow;
        int n;
        while ((n = await content.ReadAsync(buffer, ct)) != 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (progress is not null && total > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(read * 100.0 / total);
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report(100);
        return tempPath;
    }

    private static string FirstParagraph(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var para = markdown.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return para.Length > 220 ? para[..217] + "…" : para.Trim();
    }
}
