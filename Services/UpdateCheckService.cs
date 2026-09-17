using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>
/// Consumer auto-update: checks GitHub Releases on the project repo and
/// downloads the newest consumer setup exe.
///
/// - Repo constants live here; change them once when the repo moves.
/// - Uses /releases/latest, which GitHub only returns for full (non-prerelease,
///   non-draft) releases — so tagging an alpha never ships to consumers.
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

    /// <summary>Running app version ("0.1.0" style), or null when unreadable.</summary>
    public static string? CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}";
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
    /// release exists yet, the release has no consumer setup asset, or the
    /// release is not newer than the running build.
    /// </summary>
    public async Task<LatestRelease?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null; // 404 = no releases yet; 403 = rate limit

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? FirstParagraph(b.GetString()) : "";

            string? assetUrl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    if ((!name.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase)
                         && !name.StartsWith(LegacyAssetNamePrefix, StringComparison.OrdinalIgnoreCase))
                        || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    assetUrl = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                        ? u.GetString() : null;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                    break;
                }
            }

            if (string.IsNullOrEmpty(assetUrl) || !IsNewer(tag, CurrentVersion))
                return null;

            return new LatestRelease(tag.TrimStart('v', 'V'), notes, assetUrl!, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the release asset to %TEMP%. Returns the local path.</summary>
    public async Task<string> DownloadAsync(LatestRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"{AssetNamePrefix}{release.Version}.exe");
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
