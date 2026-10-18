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
using System.Globalization;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    public record AmdDriverInfo(
        string Version, 
        string DownloadUrl, 
        string SupportUrl, 
        DateTime? ReleaseDate, 
        string DisplayString);

    public class AmdDriverApiService
    {
        private static readonly HttpClient _httpClient = CreateClient();

        private static readonly HttpClient DownloadClient = CreateDownloadClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://www.amd.com/");
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        public enum AmdPackageVariant
        {
            Desktop = 0,

            Notebook = 1,
        }

        public async Task<AmdDriverInfo?> GetLatestDriverAsync(CancellationToken cancellationToken = default)
        {
            return await GetLatestDriverAsync(AmdPackageVariant.Desktop, cancellationToken);
        }

        public async Task<AmdDriverInfo?> GetLatestDriverAsync(
            AmdPackageVariant variant, CancellationToken cancellationToken = default)
        {
            try
            {
                var liveInfo = await QueryTechPowerUpFeedAsync(variant, cancellationToken);
                if (liveInfo != null)
                {
                    return liveInfo;
                }
            }
            catch (Exception)
            {
            }

            return GetCuratedLatest(variant);
        }

        private async Task<AmdDriverInfo?> QueryTechPowerUpFeedAsync(
            AmdPackageVariant variant, CancellationToken cancellationToken)
        {
            const string url = "https://www.techpowerup.com/download/amd-radeon-graphics-drivers/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html)) return null;

            var titleMatch = Regex.Match(html, @"<h3[^>]*class=""title""[^>]*>\s*AMD\s+Radeon\s+Graphics\s+Drivers\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*(WHQL)?", RegexOptions.IgnoreCase);
            if (!titleMatch.Success) return null;

            string version = titleMatch.Groups[1].Value.Trim();

            var fileMatch = Regex.Match(html, @"<div[^>]*class=""filename""[^>]*>\s*(whql-amd-software-[^<]+\.exe|non-whql-amd-software-[^<]+\.exe|amd-software-[^<]+\.exe)\s*</div>", RegexOptions.IgnoreCase);
            string filename = fileMatch.Success
                ? fileMatch.Groups[1].Value.Trim()
                : $"whql-amd-software-adrenalin-edition-{version}-win10-win11.exe";

            if (variant == AmdPackageVariant.Notebook)
            {
                filename = BuildNotebookFilename(version, filename);
            }

            DateTime? releaseDate = null;
            var dateMatch = Regex.Match(html, @"<span[^>]*class=""date""[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                string rawDate = dateMatch.Groups[1].Value.Trim();
                rawDate = Regex.Replace(rawDate, @"\b(\d+)(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);
                if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    releaseDate = parsedDate;
                }
            }

            string downloadUrl = $"https://drivers.amd.com/drivers/{filename}";
            string displaySuffix = variant == AmdPackageVariant.Notebook ? " (Notebook/combined)" : string.Empty;

            return new AmdDriverInfo(
                version,
                downloadUrl,
                "https://www.amd.com/en/support/download/drivers.html",
                releaseDate,
                $"AMD Adrenalin {version} WHQL{displaySuffix}"
            );
        }

        internal static string BuildNotebookFilename(string version, string desktopFilename)
        {
            string baseName = Path.GetFileNameWithoutExtension(desktopFilename);
            if (baseName.Contains("combined", StringComparison.OrdinalIgnoreCase))
                return desktopFilename;
            return $"{baseName}-combined.exe";
        }

        public static AmdDriverInfo GetCuratedLatest()
        {
            return GetCuratedLatest(AmdPackageVariant.Desktop);
        }

        public static AmdDriverInfo GetCuratedLatest(AmdPackageVariant variant)
        {
            const string version = "25.10.1";
            string filename = variant == AmdPackageVariant.Notebook
                ? "whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna-combined.exe"
                : "whql-amd-software-adrenalin-edition-25.10.1-win10-win11-may-rdna.exe";
            string displaySuffix = variant == AmdPackageVariant.Notebook ? " (Notebook/combined)" : string.Empty;
            return new AmdDriverInfo(
                version,
                $"https://drivers.amd.com/drivers/{filename}",
                "https://www.amd.com/en/support/download/drivers.html",
                new DateTime(2026, 8, 5),
                $"AMD Adrenalin {version} WHQL{displaySuffix}"
            );
        }

        public sealed class AmdDownloadResult
        {
            public bool Success { get; init; }
            public string? Error { get; init; }
            public long Bytes { get; init; }

            public bool Retryable { get; init; }

            public bool Resumable { get; init; }
        }

        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(30);

        private const int MaxAttempts = 4;

        public static async Task<AmdDownloadResult> DownloadInstallerAsync(
            string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                bool lastAttempt = attempt >= MaxAttempts;

                AmdDownloadResult result = await AttemptDownloadAsync(
                    url, destinationPath, progress, lastAttempt, ct);

                if (result.Success || !result.Retryable || lastAttempt)
                    return result;

                if (!result.Resumable)
                {
                    return result;
                }
            }
        }

        private static async Task<AmdDownloadResult> AttemptDownloadAsync(
            string url, string destinationPath, IProgress<double>? progress,
            bool lastAttempt, CancellationToken ct)
        {
            long alreadyHave = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
            long expectedTotal = ReadSidecarSize(destinationPath);

            bool resuming = alreadyHave > 0 && expectedTotal > alreadyHave;
            if (alreadyHave > 0 && !resuming)
            {
                TryDelete(destinationPath);
                TryDelete(SidecarPath(destinationPath));
                alreadyHave = 0;
                expectedTotal = 0;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (resuming)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(alreadyHave, null);
                    request.Headers.AcceptEncoding.Add(
                        new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));
                }

                using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(destinationPath);
                    TryDelete(SidecarPath(destinationPath));
                    return new AmdDownloadResult
                    {
                        Error = "The partial download did not match the driver AMD is offering. Starting again.",
                        Retryable = true,
                    };
                }

                response.EnsureSuccessStatusCode();

                long remaining = response.Content.Headers.ContentLength ?? 0;

                AmdExtractGate.ResumeDecision decision = AmdExtractGate.DecideResume(expectedTotal, alreadyHave, remaining);
                if (resuming && decision == AmdExtractGate.ResumeDecision.Discard)
                {
                    TryDelete(destinationPath);
                    TryDelete(SidecarPath(destinationPath));
                    return new AmdDownloadResult
                    {
                        Error = "The partial download did not match the driver AMD is offering. Starting again.",
                        Retryable = true,
                    };
                }

                bool appending = resuming && decision == AmdExtractGate.ResumeDecision.Append;
                long declaredTotal = appending ? expectedTotal : remaining;
                if (declaredTotal > 0) WriteSidecarSize(destinationPath, declaredTotal);

                if (!appending)
                {
                    string full = Path.GetFullPath(destinationPath);
                    string tempRoot = Path.GetPathRoot(full) is { Length: > 0 } root ? root : "C:\\";
                    long free = new DriveInfo(tempRoot).AvailableFreeSpace;
                    if (remaining > 0 && !AmdExtractGate.HasRoomFor(free, remaining))
                    {
                        return new AmdDownloadResult { Error = AmdExtractGate.InsufficientSpaceMessage(remaining, free) };
                    }
                }

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);

                byte[] buffer = new byte[81920];
                long written = alreadyHave;
                int lastPercent = -1;
                progress?.Report(Math.Clamp(Percent(written, declaredTotal), 0, 100));

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var destination = new FileStream(
                    destinationPath,
                    appending ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);

                if (!appending)
                {
                    byte[] magic = new byte[2];
                    stall.CancelAfter(ReadStallTimeout);
                    int got = await source.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, stall.Token);
                    if (!AmdExtractGate.IsPeExecutable(magic.AsSpan(0, got)))
                    {
                        string preview = await ReadTextPreviewAsync(source, ct);
                        TryDelete(destinationPath);
                        TryDelete(SidecarPath(destinationPath));
                        return new AmdDownloadResult
                        {
                            Error = AmdExtractGate.UnexpectedPayloadMessage(declaredTotal, preview),
                        };
                    }
                    await destination.WriteAsync(magic.AsMemory(0, got), ct);
                    written += got;
                }

                while (true)
                {
                    stall.CancelAfter(ReadStallTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return new AmdDownloadResult
                        {
                            Error = $"The download stalled at {Percent(written, declaredTotal)}% - no data received " +
                                    $"for {ReadStallTimeout.TotalSeconds:0} seconds.",
                            Retryable = true,
                            Resumable = true,
                            Bytes = written,
                        };
                    }

                    if (read <= 0) break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;

                    int percent = Percent(written, declaredTotal);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(Math.Clamp(percent, 0, 100));
                    }
                }

                if (declaredTotal > 0 && written != declaredTotal)
                {
                    return new AmdDownloadResult
                    {
                        Error = $"The download ended early ({written:N0} of {declaredTotal:N0} bytes).",
                        Retryable = true,
                        Resumable = true,
                        Bytes = written,
                    };
                }

                progress?.Report(100);
                return new AmdDownloadResult { Success = true, Bytes = written };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool canResume = File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
                return new AmdDownloadResult
                {
                    Error = lastAttempt || !canResume
                        ? $"Download failed: {ex.Message}"
                        : $"The connection dropped ({ex.Message}). Resuming...",
                    Retryable = canResume,
                    Resumable = canResume,
                    Bytes = canResume ? new FileInfo(destinationPath).Length : 0,
                };
            }
        }

        private static int Percent(long done, long total)
            => total > 0 ? (int)(done * 100 / total) : 0;

        private static string SidecarPath(string destinationPath) => destinationPath + ".size";

        private static void WriteSidecarSize(string destinationPath, long total)
        {
            try { File.WriteAllText(SidecarPath(destinationPath), total.ToString(CultureInfo.InvariantCulture)); }
            catch { }
        }

        private static long ReadSidecarSize(string destinationPath)
        {
            try
            {
                string path = SidecarPath(destinationPath);
                if (!File.Exists(path)) return 0;
                return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long total)
                    ? total
                    : 0;
            }
            catch { return 0; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static async Task<string> ReadTextPreviewAsync(Stream source, CancellationToken ct)
        {
            try
            {
                byte[] buffer = new byte[2048];
                int read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (read <= 0) return string.Empty;

                string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                string stripped = Regex.Replace(Regex.Replace(Regex.Replace(text, "<[^>]*>", " "), ">", " "), @"\s+", " ").Trim();
                if (stripped.Length > 120) stripped = stripped[..120] + "...";
                return stripped.Length == 0 ? string.Empty : $": \"{stripped}\"";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
