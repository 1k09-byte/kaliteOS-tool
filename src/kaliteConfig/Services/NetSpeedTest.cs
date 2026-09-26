// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>Download/upload throughput against Cloudflare's speed-test endpoints
/// (verified reachable from this machine). All async with cancellation; no child
/// processes. Progress reports (downMbps, upMbps, fraction 0..1).</summary>
public static class NetSpeedTest
{
    public sealed record SpeedResult(double DownMbps, double UpMbps);

    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
    private const string UpUrl = "https://speed.cloudflare.com/__up";
    private const int DownloadConnections = 4;
    private const int UploadConnections = 2;
    private const int DownloadCapSeconds = 12;
    private const int UploadCapSeconds = 8;

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static string FormatMbps(double mbps)
    {
        if (double.IsNaN(mbps) || double.IsInfinity(mbps) || mbps < 0) return "-";
        if (mbps >= 100) return $"{mbps:0} Mbps";
        if (mbps >= 10) return $"{mbps:0.#} Mbps";
        return $"{mbps:0.##} Mbps";
    }

    public static async Task<SpeedResult> RunAsync(
        Action<double, double, double>? progress = null, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(DownloadCapSeconds + UploadCapSeconds + 10));
        var lct = linked.Token;

        double down = await DownloadAsync(
            (mbps, frac) => progress?.Invoke(mbps, 0, frac * 0.6), lct).ConfigureAwait(false);
        double up = await UploadAsync(
            (mbps, frac) => progress?.Invoke(down, mbps, 0.6 + frac * 0.4), lct).ConfigureAwait(false);
        progress?.Invoke(down, up, 1.0);
        return new SpeedResult(down, up);
    }

    private static async Task<double> DownloadAsync(Action<double, double> progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(DownloadCapSeconds);
        long total = 0;
        var started = DateTime.UtcNow;
        var tasks = Enumerable.Range(0, DownloadConnections).Select(async _ =>
        {
            using var response = await Http.GetAsync(DownUrl,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[65536];
            while (DateTime.UtcNow < deadline)
            {
                int n;
                try { n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (n <= 0) break;
                Interlocked.Add(ref total, n);
                double elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                progress(total * 8.0 / elapsed / 1e6, Math.Min(1.0, elapsed / DownloadCapSeconds));
            }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        catch (HttpRequestException ex) { throw new InvalidOperationException("Download failed: " + ex.GetBaseException().Message, ex); }
        double secs = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
        if (total == 0)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new InvalidOperationException("Download moved 0 bytes - check the connection.");
        }
        return total * 8.0 / secs / 1e6;
    }

    private sealed class UploadContent : HttpContent
    {
        private readonly long _bytes;
        private readonly Action<int> _onWrite;
        private readonly Func<bool> _stop;
        public UploadContent(long bytes, Action<int> onWrite, Func<bool> stop)
        { _bytes = bytes; _onWrite = onWrite; _stop = stop; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[65536];
            long sent = 0;
            while (sent < _bytes && !_stop())
            {
                int n = (int)Math.Min(buffer.Length, _bytes - sent);
                await stream.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                sent += n;
                _onWrite(n);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _bytes; return true; }
    }

    private static async Task<double> UploadAsync(Action<double, double> progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(UploadCapSeconds);
        long total = 0;
        var started = DateTime.UtcNow;
        var tasks = Enumerable.Range(0, UploadConnections).Select(async _ =>
        {
            using var content = new UploadContent(30_000_000,
                n =>
                {
                    Interlocked.Add(ref total, n);
                    double elapsed = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    progress(total * 8.0 / elapsed / 1e6, Math.Min(1.0, elapsed / UploadCapSeconds));
                },
                () => DateTime.UtcNow >= deadline || ct.IsCancellationRequested);
            using var response = await Http.PostAsync(UpUrl, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        catch (HttpRequestException ex) { throw new InvalidOperationException("Upload failed: " + ex.GetBaseException().Message, ex); }
        double secs = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
        progress(total * 8.0 / secs / 1e6, 1.0);
        if (total == 0)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new InvalidOperationException("Upload moved 0 bytes - check the connection.");
        }
        return total * 8.0 / secs / 1e6;
    }
}
