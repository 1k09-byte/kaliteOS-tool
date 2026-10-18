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
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

public sealed class UpdateCheckService
{
    public const string Owner = "1k09-byte";
    public const string RepoName = "kaliteOS-tool";
    public const string ReleasesApiUrl = $"https://api.github.com/repos/{Owner}/{RepoName}/releases/latest";
    private const string AssetNamePrefix = "kaliteConfig-Setup-";
    private const string LegacyAssetNamePrefix = "kaliteConfig-Consumer-Setup-";
    private const string InnoUninstallRegKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{422E40DD-B1A5-4157-907F-690182582441}_is1";
    private const string KaliteOsInstallPathRegKey = @"SOFTWARE\KaliteOS";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("kaliteConfig-Consumer-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public sealed record LatestRelease(string Version, string Notes, string DownloadUrl, long SizeBytes);

    internal sealed record SetupAsset(string Name, string DownloadUrl, long SizeBytes);

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

    internal static void ClearPendingUpdate()
    {
        try { if (File.Exists(PendingMarkerPath)) File.Delete(PendingMarkerPath); }
        catch { }
    }

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

        if (string.Equals(pendingVersion, installedExeVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        return !IsNewer(pendingVersion, installedExeVersion)
               && IsNewer(installedExeVersion, runningVersion);
    }

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

    internal static string? ReadPendingUpdateVersion() => ReadPendingUpdate()?.Version;

    internal sealed record PendingUpdate(
        string Version,
        string? InstallerPath,
        string? LogPath,
        bool Reported);

    internal static string SetupLogPath(string version) => Path.Combine(
        Path.GetTempPath(), $"kaliteConfig-setup-{version.Trim()}.log");

    internal static string SetupDownloadPath(string version) => Path.Combine(
        Path.GetTempPath(), $"{AssetNamePrefix}{version.Trim()}.exe");

    internal enum UpdateAttemptState
    {
        None,
        Applied,
        AppliedElsewhere,
        Failed,
    }

    internal static UpdateAttemptState ClassifyPendingAttempt(
        PendingUpdate? pending,
        string? runningVersion,
        string? installedExeVersion)
    {
        if (pending is null || string.IsNullOrWhiteSpace(pending.Version))
            return UpdateAttemptState.None;

        if (!string.IsNullOrWhiteSpace(runningVersion) && !IsNewer(pending.Version, runningVersion))
            return UpdateAttemptState.Applied;

        if (!string.IsNullOrWhiteSpace(installedExeVersion)
            && !IsNewer(pending.Version, installedExeVersion))
            return UpdateAttemptState.AppliedElsewhere;

        return UpdateAttemptState.Failed;
    }

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

    internal static void MarkPendingUpdateReported()
    {
        var pending = ReadPendingUpdate();
        if (pending is null) return;
        WritePendingUpdate(pending.Version, pending.InstallerPath, pending.LogPath, reported: true);
    }

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

    public static string? CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch { return null; }
        }
    }

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

    public async Task<LatestRelease?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogDiag($"check: HTTP {(int)response.StatusCode} - no offer");
                return null;
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
                continue;

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

    public async Task<string> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string tempPath = SetupDownloadPath(release.Version);
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

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

}
