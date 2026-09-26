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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace kaliteConfig.Services;

/// <summary>Outcome of a thumbnail request. Failures are explicit, never a silent null.</summary>
public enum SnipThumbnailState
{
    /// <summary>Thumbnail (and optionally decoded pixels) are available.</summary>
    Ok,
    /// <summary>The caller's CancellationToken fired (scrolled out of view / recycled card).</summary>
    Cancelled,
    /// <summary>The source file no longer exists (deleted outside the app).</summary>
    MissingFile,
    /// <summary>Decode/encode/IO failed; <see cref="SnipThumbnailResult.Error"/> has the real reason.</summary>
    Failed,
}

public sealed class SnipThumbnailResult
{
    public SnipThumbnailState State { get; init; }
    /// <summary>PNG in the on-disk cache (null when the request failed).</summary>
    public string? CachePath { get; init; }
    /// <summary>Decoded, tightly packed premultiplied BGRA pixels of the tier (only when requested).</summary>
    public byte[]? Bgra { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Longest-edge tier this thumbnail was generated for.</summary>
    public int Tier { get; init; }
    public bool FromDiskCache { get; init; }
    public bool FromMemoryCache { get; init; }
    public string? Error { get; init; }

    /// <summary>True when every pixel is identical: protected/DRM content legitimately captures a
    /// flat frame, so this is a warning badge, never a hidden card.</summary>
    public bool LooksBlank { get; init; }

    public bool IsOk => State == SnipThumbnailState.Ok;

    /// <summary>Short, user-facing reason for a failed card.</summary>
    public string ShortError => State switch
    {
        SnipThumbnailState.MissingFile => "File missing",
        SnipThumbnailState.Cancelled => "Cancelled",
        _ => string.IsNullOrWhiteSpace(Error) ? "Preview failed" : Error!,
    };
}

/// <summary>
/// ONE screenshot preview pipeline for the whole Snip feature (gallery cards, recent
/// snips, details panel, viewer, editor, pin window). Everything that shows a snip goes
/// through here so no two surfaces can disagree.
///
/// Tiers: 256 / 512 / 1024 px on the longest edge. Callers ask in PHYSICAL pixels
/// (DIP size x XamlRoot.RasterizationScale) and get the smallest tier that covers them.
///
/// - Disk cache: %LocalAppData%\kaliteConfig\SnipCache\thumbs, keyed by
///   hash(path + lastWriteTimeUtc + length + tier) so editing a file never yields a stale thumb.
/// - Memory cache: LRU with a byte budget, holding decoded tier pixels only.
/// - In-flight de-dupe: the same key requested twice awaits one Task.
/// - Decodes run off the UI thread, capped at 2-4 concurrent, with retries and FileShare.Read.
/// </summary>
public static class SnipThumbnailService
{
    /// <summary>Longest-edge sizes, ascending. Never upscale into a tier.</summary>
    public static readonly int[] Tiers = { 256, 512, 1024 };

    public const int SmallTier = 256;
    public const int DefaultTier = 512;
    public const int LargeTier = 1024;

    private const long MemoryBudgetBytes = 128L * 1024 * 1024;
    private const int IoRetries = 4;
    private const int IoRetryDelayMs = 60;

    private static readonly SemaphoreSlim DecodeGate =
        new(Math.Clamp(Environment.ProcessorCount / 4, 2, 4));

    private static readonly ConcurrentDictionary<string, Task<SnipThumbnailResult>> InFlight =
        new(StringComparer.Ordinal);

    private static readonly BgraLruCache Memory = new(MemoryBudgetBytes);
    private static readonly ConcurrentDictionary<string, string> LastErrors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IndexLock = new();
    private static bool _cleanupDone;

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "SnipCache", "thumbs");

    private static string IndexPath => Path.Combine(CacheDirectory, "index.json");

    // ---------------------------------------------------------------- sizing

    /// <summary>Physical pixels needed for a DIP size at a rasterization scale.</summary>
    public static int RequiredPhysicalPixels(double dipSize, double rasterizationScale)
    {
        if (dipSize <= 0) return 0;
        var scale = rasterizationScale <= 0 ? 1.0 : rasterizationScale;
        return (int)Math.Ceiling(dipSize * scale);
    }

    /// <summary>Smallest tier that is >= the required physical size (largest tier when bigger).</summary>
    public static int PickTier(int requiredPhysicalPixels)
    {
        foreach (var tier in Tiers)
        {
            if (tier >= requiredPhysicalPixels) return tier;
        }
        return Tiers[^1];
    }

    /// <summary>Convenience: tier for a DIP size at a rasterization scale.</summary>
    public static int PickTierForDip(double dipSize, double rasterizationScale) =>
        PickTier(RequiredPhysicalPixels(dipSize, rasterizationScale));

    // ------------------------------------------------------------ public API

    /// <summary>Real error text for the last failed request of a file (used by card states).</summary>
    public static string? GetLastError(string filePath) =>
        string.IsNullOrWhiteSpace(filePath) ? null
        : LastErrors.TryGetValue(filePath, out var e) ? e : null;

    public static void ClearMemoryCache() => Memory.Clear();

    /// <summary>Cheap, per-request-cancellable thumbnail lookup. Never throws.</summary>
    public static Task<SnipThumbnailResult> GetAsync(
        string filePath, int tier, bool includePixels = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(null, "No file path."));

        tier = Tiers.Contains(tier) ? tier : PickTier(tier);

        if (!File.Exists(filePath))
            return Task.FromResult(new SnipThumbnailResult
            {
                State = SnipThumbnailState.MissingFile,
                Tier = tier,
                Error = "The file no longer exists at that path.",
            });

        string key;
        try { key = CacheKey(filePath, tier); }
        catch (Exception ex) { return Task.FromResult(Fail(filePath, ex.Message)); }

        if (includePixels && Memory.TryGet(key, out var memPixels, out var mw, out var mh, out var mtier, out var mBlank))
        {
            return Task.FromResult(new SnipThumbnailResult
            {
                State = SnipThumbnailState.Ok,
                CachePath = CacheFileFor(key),
                Bgra = memPixels,
                Width = mw,
                Height = mh,
                Tier = mtier,
                LooksBlank = mBlank,
                FromMemoryCache = true,
                FromDiskCache = true,
            });
        }

        // De-dupe: the same key shares one decode task; each caller still waits with its own token.
        var shared = InFlight.GetOrAdd(key, k => StartShared(k, filePath, tier, includePixels));
        return AwaitSharedAsync(shared, filePath, ct);
    }

    /// <summary>
    /// Generates every tier from an in-memory rendered bitmap (the save/export path), so a
    /// fresh snip has thumbnails before it can ever appear in the gallery, and no re-decode
    /// is needed. Uses one area-averaging pass per tier from the full-resolution source.
    /// </summary>
    public static async Task<IReadOnlyList<SnipThumbnailResult>> GenerateFromPixelsAsync(
        string filePath, byte[] bgra, int width, int height, CancellationToken ct = default)
    {
        var results = new List<SnipThumbnailResult>();
        if (string.IsNullOrWhiteSpace(filePath) || bgra is null || width <= 0 || height <= 0)
            return results;

        var longest = Math.Max(width, height);
        var expected = width * height * 4;
        if (bgra.Length < expected) return results;

        await DecodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A 200 px image only needs the 256 tier: larger tiers would be upscaling.
            foreach (var tier in Tiers.Where(t => t >= longest))
            {
                ct.ThrowIfCancellationRequested();
                var (tw, th) = Fit(width, height, tier);
                var pixels = (tw == width && th == height)
                    ? bgra
                    : DownscaleBgraArea(bgra, width, height, tw, th);
                await StoreTierAsync(filePath, tier, pixels, tw, th).ConfigureAwait(false);
                results.Add(new SnipThumbnailResult
                {
                    State = SnipThumbnailState.Ok,
                    CachePath = CacheFileFor(CacheKey(filePath, tier)),
                    Bgra = pixels,
                    Width = tw, Height = th, Tier = tier,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Caller gave up; partial tiers stay cached and are reusable.
        }
        catch (Exception ex)
        {
            RecordError(filePath, ex.Message);
        }
        finally
        {
            DecodeGate.Release();
        }
        return results;
    }

    /// <summary>Generates the small/medium tiers from the file itself (import + backfill).</summary>
    public static async Task EnsureThumbnailsAsync(string filePath, CancellationToken ct = default)
    {
        foreach (var tier in new[] { SmallTier, DefaultTier })
        {
            var r = await GetAsync(filePath, tier, includePixels: false, ct).ConfigureAwait(false);
            if (r.State == SnipThumbnailState.Cancelled) return;
        }
    }

    /// <summary>
    /// Cheap flat-frame test on decoded tier pixels: compares the first pixel against a strided
    /// sample (full scan for small buffers), so it costs almost nothing on the load path.
    /// </summary>
    public static bool IsEffectivelyBlank(byte[] bgra, int width, int height)
    {
        if (bgra is null || bgra.Length < 4) return false;
        if (width <= 0 || height <= 0) return false;

        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2], a0 = bgra[3];
        var pixels = width * height;
        if (pixels * 4 > bgra.Length) pixels = bgra.Length / 4;

        var step = pixels <= 4096 ? 1 : Math.Max(1, pixels / 4096);
        for (int i = 0; i < pixels; i += step)
        {
            int p = i * 4;
            if (bgra[p] != b0 || bgra[p + 1] != g0 || bgra[p + 2] != r0 || bgra[p + 3] != a0)
                return false;
        }
        return true;
    }

    /// <summary>Makes the next request re-decode from the source (Retry on a failed card).</summary>
    public static void Invalidate(string filePath) => DeleteCachedThumbnails(filePath);

    /// <summary>Drops every cached tier of a file (rename, move, delete, overwrite).</summary>
    public static void DeleteCachedThumbnails(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        foreach (var tier in Tiers)
        {
            string key;
            try { key = CacheKey(filePath, tier); }
            catch { continue; }

            var path = CacheFileFor(key);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            Memory.Remove(key);
            RemoveIndexEntry(key);
        }
    }

    /// <summary>Deletes the whole disk + memory cache ("Rebuild thumbnails").</summary>
    public static void ClearAllCaches()
    {
        Memory.Clear();
        try
        {
            var dir = CacheDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            if (File.Exists(IndexPath)) File.Delete(IndexPath);
        }
        catch { }
    }

    /// <summary>Deletes cache entries whose source is gone or changed, plus unreferenced files.</summary>
    public static int CleanupOrphans()
    {
        int removed = 0;
        try
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir)) return 0;

            var index = LoadIndex();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirty = false;

            foreach (var key in index.Keys.ToList())
            {
                var entry = index[key];
                var stale = true;
                try
                {
                    var fi = new FileInfo(entry.Source);
                    stale = !fi.Exists || fi.Length != entry.Length || fi.LastWriteTimeUtc.Ticks != entry.WriteUtcTicks;
                }
                catch { stale = true; }

                if (stale)
                {
                    try { File.Delete(CacheFileFor(key)); } catch { }
                    index.Remove(key);
                    dirty = true;
                    removed++;
                }
                else
                {
                    referenced.Add(CacheFileFor(key));
                }
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            {
                if (referenced.Contains(f)) continue;
                try { File.Delete(f); removed++; } catch { }
            }

            if (dirty) SaveIndex(index);
        }
        catch { }
        return removed;
    }

    /// <summary>Runs orphan cleanup once per session (called from page navigation).</summary>
    public static Task CleanupOrphansOnceAsync()
    {
        if (_cleanupDone) return Task.CompletedTask;
        _cleanupDone = true;
        return Task.Run(CleanupOrphans);
    }

    // --------------------------------------------------------------- decoding

    private static Task<SnipThumbnailResult> StartShared(
        string key, string filePath, int tier, bool includePixels)
    {
        var task = Task.Run(() => LoadOrGenerateAsync(filePath, tier, key, includePixels));
        _ = task.ContinueWith(t =>
        {
            InFlight.TryRemove(key, out _);
            if (t.IsFaulted && t.Exception is not null) RecordError(filePath, t.Exception.GetBaseException().Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static async Task<SnipThumbnailResult> AwaitSharedAsync(
        Task<SnipThumbnailResult> shared, string? filePath, CancellationToken ct)
    {
        try
        {
            return ct.CanBeCanceled ? await shared.WaitAsync(ct).ConfigureAwait(false) : await shared.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SnipThumbnailResult
            {
                State = SnipThumbnailState.Cancelled,
                Error = "Request cancelled (item scrolled out of view).",
            };
        }
        catch (Exception ex)
        {
            return Fail(filePath, ex.GetBaseException().Message);
        }
    }

    private static async Task<SnipThumbnailResult> LoadOrGenerateAsync(
        string filePath, int tier, string key, bool includePixels)
    {
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cachePath = CacheFileFor(key);
            byte[]? pixels = null;
            int w = 0, h = 0;
            var fromDisk = false;

            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = await DecodePixelsAsync(await ReadAllBytesWithRetryAsync(cachePath).ConfigureAwait(false), tier)
                        .ConfigureAwait(false);
                    pixels = cached.Pixels; w = cached.Width; h = cached.Height;
                    fromDisk = true;
                }
                catch
                {
                    // A corrupt/partial cache entry is dropped and regenerated below.
                    pixels = null;
                    try { File.Delete(cachePath); } catch { }
                }
            }

            if (pixels is null)
            {
                if (!File.Exists(filePath))
                {
                    return new SnipThumbnailResult
                    {
                        State = SnipThumbnailState.MissingFile,
                        Tier = tier,
                        Error = "The file no longer exists at that path.",
                    };
                }

                var bytes = await ReadAllBytesWithRetryAsync(filePath).ConfigureAwait(false);
                var decoded = await DecodePixelsAsync(bytes, tier).ConfigureAwait(false);
                pixels = decoded.Pixels; w = decoded.Width; h = decoded.Height;

                await StoreTierAsync(filePath, tier, pixels, w, h, key).ConfigureAwait(false);
                fromDisk = true;
            }

            var blank = IsEffectivelyBlank(pixels, w, h);
            if (includePixels) Memory.Set(key, pixels, w, h, tier, blank);
            LastErrors.TryRemove(filePath, out _);

            return new SnipThumbnailResult
            {
                State = SnipThumbnailState.Ok,
                CachePath = cachePath,
                Bgra = includePixels ? pixels : null,
                Width = w,
                Height = h,
                Tier = tier,
                LooksBlank = blank,
                FromDiskCache = fromDisk,
            };
        }
        catch (Exception ex)
        {
            RecordError(filePath, ex.GetBaseException().Message);
            return Fail(filePath, ex.GetBaseException().Message);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// Decodes BGRA8 premultiplied pixels, downscaled to <paramref name="maxEdge"/> with
    /// area-averaging (BitmapInterpolationMode.Fant) and EXIF orientation applied.
    /// Pixels are never upscaled. Returns void pixels when anything goes wrong.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> DecodePixelsAsync(
        byte[] fileBytes, int maxEdge)
    {
        using var ms = new MemoryStream(fileBytes, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());

        uint sw = decoder.PixelWidth, sh = decoder.PixelHeight;
        if (sw == 0 || sh == 0) throw new InvalidDataException("The image has no pixels.");

        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(sw, sh));
        if (scale < 1.0)
        {
            transform.ScaledWidth = (uint)Math.Max(1, (int)Math.Round(sw * scale));
            transform.ScaledHeight = (uint)Math.Max(1, (int)Math.Round(sh * scale));
        }

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,   // averaging premultiplied avoids dark fringes on alpha
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = data.DetachPixelData();
        int w = (int)(scale < 1.0 ? transform.ScaledWidth : sw);
        int h = (int)(scale < 1.0 ? transform.ScaledHeight : sh);
        return (pixels, w, h);
    }

    /// <summary>Encodes a tier PNG next to the cache and records it in the index.</summary>
    private static async Task StoreTierAsync(
        string filePath, int tier, byte[] pixels, int width, int height, string? knownKey = null)
    {
        var key = knownKey ?? CacheKey(filePath, tier);
        var cachePath = CacheFileFor(key);
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) return;

        Directory.CreateDirectory(CacheDirectory);
        await WritePngAtomicAsync(cachePath, pixels, width, height).ConfigureAwait(false);
        AddIndexEntry(key, filePath, tier);
    }

    /// <summary>Writes a PNG to a temp file then renames it into place (readers never see a partial file).</summary>
    public static async Task WritePngAtomicAsync(string destPath, byte[] bgra, int width, int height)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)width, (uint)height, 96, 96, bgra);
                await encoder.FlushAsync();
            }
            File.Move(tmp, destPath, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Reads a file allowing concurrent readers (FileShare.Read) with a short retry/backoff,
    /// so a writer or scanner holding the file briefly cannot produce a blank card.
    /// </summary>
    public static async Task<byte[]> ReadAllBytesWithRetryAsync(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= IoRetries; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = fs.Length;
                if (length == 0) throw new InvalidDataException("The file is empty.");
                var buffer = new byte[length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(read), CancellationToken.None);
                    if (n == 0) break;
                    read += n;
                }
                if (read == 0) throw new InvalidDataException("The file could not be read.");
                return buffer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < IoRetries)
                    await Task.Delay(IoRetryDelayMs * (attempt + 1)).ConfigureAwait(false);
            }
        }
        throw new IOException($"Could not read '{Path.GetFileName(path)}' after {IoRetries + 1} attempts: {last?.Message}", last);
    }

    // ------------------------------------------------------- downscale (memory)

    /// <summary>Longest edge = <paramref name="maxEdge"/>, never upscaling.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxEdge || longest == 0) return (width, height);
        double s = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }

    /// <summary>
    /// Separable area-average (box) downscale of premultiplied BGRA. Equivalent math to
    /// BitmapInterpolationMode.Fant, so save-time (in-memory) and lazy (decoder) thumbnails agree.
    /// </summary>
    public static byte[] DownscaleBgraArea(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0) return Array.Empty<byte>();
        if (dw == sw && dh == sh) return src;
        if (src.Length < sw * sh * 4) throw new ArgumentException("Source buffer is smaller than the source size.");

        // Pass 1: horizontal, source rows are unchanged in count.
        var mid = new float[dw * sh * 4];
        var (xStart, xWeights) = BuildWeights(sw, dw);
        for (int y = 0; y < sh; y++)
        {
            int srcRow = y * sw * 4;
            int dstRow = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                float a = 0, r = 0, g = 0, b = 0, total = 0;
                int from = xStart[x], to = xStart[x + 1];
                for (int i = from; i < to; i++)
                {
                    float weight = xWeights[i];
                    int p = srcRow + i * 4;
                    b += src[p] * weight;
                    g += src[p + 1] * weight;
                    r += src[p + 2] * weight;
                    a += src[p + 3] * weight;
                    total += weight;
                }
                if (total <= 0) total = 1;
                int d = dstRow + x * 4;
                mid[d] = b / total; mid[d + 1] = g / total; mid[d + 2] = r / total; mid[d + 3] = a / total;
            }
        }

        // Pass 2: vertical.
        var dst = new byte[dw * dh * 4];
        var (yStart, yWeights) = BuildWeights(sh, dh);
        for (int y = 0; y < dh; y++)
        {
            int from = yStart[y], to = yStart[y + 1];
            for (int x = 0; x < dw; x++)
            {
                float a = 0, r = 0, g = 0, b = 0, total = 0;
                for (int j = from; j < to; j++)
                {
                    float weight = yWeights[j];
                    int p = (j * dw + x) * 4;
                    b += mid[p] * weight;
                    g += mid[p + 1] * weight;
                    r += mid[p + 2] * weight;
                    a += mid[p + 3] * weight;
                    total += weight;
                }
                if (total <= 0) total = 1;
                int d = (y * dw + x) * 4;
                dst[d] = Clamp(b / total); dst[d + 1] = Clamp(g / total);
                dst[d + 2] = Clamp(r / total); dst[d + 3] = Clamp(a / total);
            }
        }
        return dst;
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>Fractional-coverage weights: destination span [start[i], start[i+1]) over source samples.</summary>
    private static (int[] Start, float[] Weights) BuildWeights(int source, int dest)
    {
        var start = new int[dest + 1];
        var weights = new float[source];
        double ratio = (double)source / dest;
        int last = 0;
        for (int i = 0; i < dest; i++)
        {
            double from = i * ratio, to = (i + 1) * ratio;
            int s = (int)Math.Floor(from), e = (int)Math.Ceiling(to);
            if (e <= s) e = s + 1;
            if (e > source) e = source;
            start[i] = s;
            last = Math.Max(last, e);
            for (int j = s; j < e; j++)
            {
                double overlap = Math.Min(j + 1.0, to) - Math.Max((double)j, from);
                weights[j] = (float)Math.Max(0.0, overlap);
            }
        }
        start[dest] = Math.Max(source > 0 ? source : 0, last);
        return (start, weights);
    }

    // ------------------------------------------------------------------ cache

    public static string CacheKey(string filePath, int tier)
    {
        var fi = new FileInfo(filePath);
        var stamp = fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}" : "missing";
        var raw = $"{filePath.ToLowerInvariant()}|{stamp}|{tier}";
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)))[..20];
        return $"{hash}_{tier}";
    }

    private static string CacheFileFor(string key) => Path.Combine(CacheDirectory, key + ".png");

    private sealed class IndexEntry
    {
        public string Source { get; set; } = "";
        public int Tier { get; set; }
        public long Length { get; set; }
        public long WriteUtcTicks { get; set; }
    }

    private static Dictionary<string, IndexEntry> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(json)
                   ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static void SaveIndex(Dictionary<string, IndexEntry> index)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(index));
        }
        catch { }
    }

    private static void AddIndexEntry(string key, string filePath, int tier)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return;
            lock (IndexLock)
            {
                var index = LoadIndex();
                index[key] = new IndexEntry
                {
                    Source = filePath,
                    Tier = tier,
                    Length = fi.Length,
                    WriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                };
                SaveIndex(index);
            }
        }
        catch { }
    }

    private static void RemoveIndexEntry(string key)
    {
        try
        {
            lock (IndexLock)
            {
                var index = LoadIndex();
                if (index.Remove(key)) SaveIndex(index);
            }
        }
        catch { }
    }

    private static void RecordError(string? filePath, string message)
    {
        if (!string.IsNullOrWhiteSpace(filePath)) LastErrors[filePath] = message;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "snip-thumb.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] thumb FAIL {Path.GetFileName(filePath ?? "?")}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static SnipThumbnailResult Fail(string? filePath, string message) => new()
    {
        State = SnipThumbnailState.Failed,
        Error = message,
    };
}

/// <summary>Bounded LRU of decoded tier pixels (BGRA), evicted by total byte size.</summary>
internal sealed class BgraLruCache
{
    private sealed class Entry
    {
        public string Key = "";
        public byte[] Data = Array.Empty<byte>();
        public int Width, Height, Tier;
        public bool Blank;
    }

    private readonly long _budget;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private long _bytes;

    public BgraLruCache(long budgetBytes) => _budget = Math.Max(1, budgetBytes);

    public long Bytes
    {
        get { lock (_lock) return _bytes; }
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public bool TryGet(string key, out byte[] data, out int width, out int height, out int tier, out bool blank)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                data = node.Value.Data; width = node.Value.Width; height = node.Value.Height;
                tier = node.Value.Tier; blank = node.Value.Blank;
                return true;
            }
        }
        data = Array.Empty<byte>(); width = 0; height = 0; tier = 0; blank = false;
        return false;
    }

    public void Set(string key, byte[] data, int width, int height, int tier, bool blank)
    {
        if (data is null || data.Length == 0) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Value.Data.Length;
                _order.Remove(existing);
                _map.Remove(key);
            }

            var entry = new Entry { Key = key, Data = data, Width = width, Height = height, Tier = tier, Blank = blank };
            var node = _order.AddFirst(entry);
            _map[key] = node;
            _bytes += data.Length;

            while (_bytes > _budget && _order.Last is not null)
            {
                var last = _order.Last;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Data.Length;
            }
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _bytes -= node.Value.Data.Length;
                _order.Remove(node);
                _map.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }
}
