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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace kaliteConfig.Services;

public static class SnipGalleryService
{
    private const string ThumbsDirName = "_thumbs";
    private const string TrashDirName = "_trash";

    /// <summary>How long a file must sit untouched before the background poll considers it
    /// finished (see <see cref="GetSnipSignatureAsync"/>).</summary>
    public static readonly TimeSpan QuietSettleWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Snips live in Pictures\kaliteConfig Snips (user-visible). AppData is kept
    /// only for the thumbnail cache and trash, which live under the legacy folder.
    /// Existing snips in the old AppData location are migrated once, automatically.
    /// </summary>
    public static string GetSnipsDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var dir = Path.Combine(string.IsNullOrEmpty(pictures)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Pictures"
            : pictures, "kaliteConfig Snips");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        MigrateLegacySnips(dir);
        return dir;
    }

    private static string LegacySnipsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Snips");

    private static bool _migrationChecked;
    private static void MigrateLegacySnips(string newDir)
    {
        if (_migrationChecked) return;
        _migrationChecked = true;
        try
        {
            var legacy = LegacySnipsDirectory;
            if (!Directory.Exists(legacy) || string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue; // leave thumbs/trash/meta behind
                var dest = Path.Combine(newDir, Path.GetFileName(file));
                var n = 1;
                while (File.Exists(dest)) dest = Path.Combine(newDir, $"{Path.GetFileNameWithoutExtension(file)}_{n++}{ext}");
                File.Move(file, dest);
                var meta = file + ".meta.json";
                if (File.Exists(meta)) { try { File.Copy(meta, dest + ".meta.json", true); } catch { } }
            }
        }
        catch { /* migration is best-effort; old files remain in AppData */ }
    }

    public static string GetThumbsDirectory()
    {
        var dir = Path.Combine(GetSnipsDirectory(), ThumbsDirName);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetTrashDirectory()
    {
        var dir = Path.Combine(GetSnipsDirectory(), TrashDirName);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MetaPathFor(string snipPath) => snipPath + ".meta.json";

    public static async Task<SnipMeta?> LoadMetaAsync(string snipPath)
    {
        try
        {
            var metaPath = MetaPathFor(snipPath);
            if (!File.Exists(metaPath)) return null;
            var json = await File.ReadAllTextAsync(metaPath);
            return System.Text.Json.JsonSerializer.Deserialize<SnipMeta>(json);
        }
        catch
        {
            return null;
        }
    }

    public static async Task SaveMetaAsync(string snipPath, SnipMeta meta)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(meta);
            await File.WriteAllTextAsync(MetaPathFor(snipPath), json);
        }
        catch { }
    }

    /// <summary>
    /// Cheap fingerprint of the snips folder: every snip plus its length and write time, and the
    /// same for metadata sidecars. No metadata parsing and no image work.
    ///
    /// A background poll compares this first, so an unchanged folder costs one directory
    /// enumeration and nothing else -- which is what lets the poll run often enough to feel
    /// instant without ever disturbing the page.
    /// </summary>
    public static async Task<string> GetSnipSignatureAsync()
    {
        return await Task.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var dir = GetSnipsDirectory();
                var thumbs = GetThumbsDirectory();
                var trash = GetTrashDirectory();
                var settled = DateTime.UtcNow - QuietSettleWindow;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (file.StartsWith(thumbs, StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith(trash, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(file);
                    var isMeta = name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase);
                    if (!isMeta && !SnipGalleryQuery.SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        continue;
                    try
                    {
                        var info = new FileInfo(file);
                        // A file written moments ago may still be mid-encode: leaving it out of the
                        // fingerprint means we pick it up on the next poll instead of building a
                        // card for a half-written image that would fail to decode and stay failed.
                        if (info.LastWriteTimeUtc > settled) continue;
                        sb.Append(name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                    }
                    catch { }
                }
            }
            catch { }
            return sb.ToString();
        });
    }

    public static async Task<List<SnipEntry>> GetAllSnipEntriesAsync()
    {
        return await Task.Run(async () =>
        {
            var dir = GetSnipsDirectory();
            var thumbs = GetThumbsDirectory();
            var trash = GetTrashDirectory();
            var list = new List<SnipEntry>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                    if (file.StartsWith(thumbs, StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith(trash, StringComparison.OrdinalIgnoreCase)) continue;

                    var meta = await LoadMetaAsync(file);
                    var createdUtc = meta?.CreatedUtc ?? File.GetCreationTimeUtc(file);
                    var entry = SnipEntry.FromMeta(file, new FileInfo(file).Length, meta, createdUtc);
                    list.Add(entry);
                }
            }
            catch { }
            return list;
        });
    }

    public static async Task<(int Count, long Bytes)> GetStorageStatsAsync()
    {
        return await Task.Run(() =>
        {
            var dir = GetSnipsDirectory();
            long bytes = 0;
            int count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                    if (file.Contains(ThumbsDirName, StringComparison.OrdinalIgnoreCase) ||
                        file.Contains(TrashDirName, StringComparison.OrdinalIgnoreCase)) continue;
                    try { bytes += new FileInfo(file).Length; count++; } catch { }
                }
            }
            catch { }
            return (count, bytes);
        });
    }

    public static async Task<List<string>> GetAllSnipFilesAsync()
    {
        var entries = await GetAllSnipEntriesAsync();
        return entries.Select(e => e.FilePath).ToList();
    }

    public static string ThumbPathFor(string snipPath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(snipPath.ToLowerInvariant()));
        var hex = Convert.ToHexString(hashBytes)[..16];
        return Path.Combine(GetThumbsDirectory(), hex + ".png");
    }

    public static async Task<bool> ConvertImageAsync(string srcPath, string destDir, string targetExtension)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                var ext = targetExtension.ToLowerInvariant();
                if (!ext.StartsWith(".")) ext = "." + ext;
                var dest = Path.Combine(destDir, Path.GetFileNameWithoutExtension(srcPath) + ext);

                var storageFile = await StorageFile.GetFileFromPathAsync(srcPath);
                using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);

                using var outStream2 = File.Create(dest);
                var encoderId = ext switch
                {
                    ".jpg" or ".jpeg" => BitmapEncoder.JpegEncoderId,
                    ".bmp" => BitmapEncoder.BmpEncoderId,
                    _ => BitmapEncoder.PngEncoderId,
                };
                var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream2.AsRandomAccessStream());
                var frame = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                encoder.SetSoftwareBitmap(frame);
                await encoder.FlushAsync();

                var metaSrc = srcPath + ".meta.json";
                if (File.Exists(metaSrc))
                {
                    try { System.IO.File.Copy(metaSrc, dest + ".meta.json", true); } catch { }
                }
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>Resizes an image (same format) into destDir, named stem_WxH.ext.
    /// Zero for an axis means auto; returns null when nothing could be done.</summary>
    public static async Task<string?> ResizeImageAsync(string srcPath, string destDir, int reqW, int reqH, bool aspectLock)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                var ext = Path.GetExtension(srcPath).ToLowerInvariant();

                var storageFile = await StorageFile.GetFileFromPathAsync(srcPath);
                using var stream = await storageFile.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var (w, h) = SnipGalleryQuery.ComputeResizeDims(
                    (int)decoder.PixelWidth, (int)decoder.PixelHeight, reqW, reqH, aspectLock);
                if (w <= 0 || h <= 0) return null;

                var dest = Path.Combine(destDir,
                    $"{Path.GetFileNameWithoutExtension(srcPath)}_{w}x{h}{ext}");
                var n = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(destDir,
                        $"{Path.GetFileNameWithoutExtension(srcPath)}_{w}x{h}_{n++}{ext}");

                // Decoder stream was only used for headers so far; rewind for pixels.
                stream.Seek(0);
                var decoder2 = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)w,
                    ScaledHeight = (uint)h,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };
                var frame = await decoder2.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    transform, ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                using var outStream = File.Create(dest);
                var encoderId = ext switch
                {
                    ".jpg" or ".jpeg" => BitmapEncoder.JpegEncoderId,
                    ".bmp" => BitmapEncoder.BmpEncoderId,
                    _ => BitmapEncoder.PngEncoderId,
                };
                var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(frame);
                await encoder.FlushAsync();
                return dest;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>Outcome of a thumbnail load: the image, the ORIGINAL image size (for the card
    /// caption), the decoded tier/pixel size (for 1:1 decisions), whether the frame looks blank,
    /// and on failure the real reason - never a silent blank card.</summary>
    public sealed record SnipThumbnailLoad(
        BitmapImage? Image, int Width, int Height, string? Error, string? CachePath,
        int Tier = 0, int PixelWidth = 0, int PixelHeight = 0, bool LooksBlank = false, bool Missing = false)
    {
        public bool Ok => Image != null;
    }

    /// <summary>
    /// THE thumbnail entry point for every preview surface. Routes through
    /// <see cref="SnipThumbnailService"/> (tiers + disk/memory cache + cancellation) so the
    /// gallery grid, recent snips, details panel and viewer always agree.
    /// </summary>
    public static async Task<SnipThumbnailLoad> LoadThumbnailResultAsync(
        string filePath, int requiredPixels = SnipThumbnailService.DefaultTier, System.Threading.CancellationToken ct = default)
    {
        // BitmapImage has UI-thread affinity, but Task awaits hop threads (WinUI 3 has no
        // SynchronizationContext for them). Capture the UI queue up front and marshal the
        // decode back explicitly: creating it on a pool thread throws RPC_E_WRONG_THREAD
        // and yields gray tiles. Null when headless (tests run inline).
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            var tier = SnipThumbnailService.PickTier(requiredPixels);
            var thumb = await SnipThumbnailService.GetAsync(filePath, tier, includePixels: false, ct).ConfigureAwait(false);
            if (!thumb.IsOk)
            {
                return new SnipThumbnailLoad(null, 0, 0, thumb.ShortError, null,
                    Tier: tier, Missing: thumb.State == SnipThumbnailState.MissingFile);
            }

            // BitmapImage is a XAML object: this part must run on the UI thread (see above).
            var file = await StorageFile.GetFileFromPathAsync(thumb.CachePath!).AsTask(ct).ConfigureAwait(false);
            var bmp = ui is null
                ? await DecodeBitmapAsync(file, ct).ConfigureAwait(false)
                : await RunOnUiAsync(ui, ct, () => DecodeBitmapAsync(file, ct)).ConfigureAwait(false);

            var (w, h) = await GetOriginalDimensionsAsync(filePath).ConfigureAwait(false);
            return new SnipThumbnailLoad(bmp, w, h, null, thumb.CachePath,
                thumb.Tier, thumb.Width, thumb.Height, thumb.LooksBlank);
        }
        catch (OperationCanceledException)
        {
            return new SnipThumbnailLoad(null, 0, 0, null, null);
        }
        catch (Exception ex)
        {
            var reason = SnipThumbnailService.GetLastError(filePath) ?? ex.Message;
            var missing = !File.Exists(filePath);
            return new SnipThumbnailLoad(null, 0, 0, missing ? "File missing" : reason, null, Missing: missing);
        }
    }

    private const int DecodeTimeoutSeconds = 8;

    private static System.Threading.Tasks.Task<T> RunOnUiAsync<T>(
        Microsoft.UI.Dispatching.DispatcherQueue ui, System.Threading.CancellationToken ct,
        Func<System.Threading.Tasks.Task<T>> work)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return tcs.Task; }
        if (!ui.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await work().ConfigureAwait(true)); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("UI queue is shutting down."));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Decodes a cached tier file into a <see cref="BitmapImage"/> and does not return until the
    /// pixels are actually resident.
    ///
    /// This is the fix for "the preview only shows up after I take a screenshot":
    /// <c>SetSourceAsync</c> returns as soon as the stream has been read, while the decode itself
    /// completes later on a worker thread. The stream was disposed at that point, so the card
    /// received a pixel-less BitmapImage: the Image element had no natural size, collapsed to
    /// 0x0, and stayed invisible until something outside the app forced a fresh layout pass
    /// (taking a screenshot re-activating the window). Awaiting ImageOpened with the stream still
    /// open means every caller gets a renderable image - or a real error, never a silent blank.
    /// </summary>
    private static async Task<BitmapImage> DecodeBitmapAsync(StorageFile file, CancellationToken ct)
    {
        using var stream = await file.OpenReadAsync();
        var bmp = new BitmapImage();
        var opened = new TaskCompletionSource<bool>();
        bmp.ImageOpened += (_, _) => opened.TrySetResult(true);
        bmp.ImageFailed += (_, args) => opened.TrySetException(new InvalidDataException(
            string.IsNullOrWhiteSpace(args.ErrorMessage) ? "The preview could not be decoded." : args.ErrorMessage));

        await bmp.SetSourceAsync(stream);

        // A warm OS image cache can decode synchronously: only wait when the pixels are not there yet.
        if (bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
        {
            try
            {
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(DecodeTimeoutSeconds), ct).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                throw new InvalidDataException($"The preview did not finish decoding within {DecodeTimeoutSeconds} s.");
            }
        }

        return bmp;
    }

    /// <summary>Original pixel size from the meta sidecar, falling back to a header-only decode.</summary>
    public static async Task<(int Width, int Height)> GetOriginalDimensionsAsync(string filePath)
    {
        try
        {
            var meta = await LoadMetaAsync(filePath);
            if (meta != null && meta.Width > 0 && meta.Height > 0)
                return (meta.Width, meta.Height);
        }
        catch { }
        return await GetImageDimensionsAsync(filePath);
    }

    /// <summary>Ensures a cached thumbnail exists and returns its path (no XAML objects created).</summary>
    public static async Task<(string? Path, int Width, int Height)> GetThumbnailPathAsync(string filePath, int decodeWidth = 320)
    {
        var (w, h) = await GetOriginalDimensionsAsync(filePath);
        var thumb = await SnipThumbnailService.GetAsync(filePath, SnipThumbnailService.PickTier(decodeWidth));
        return (thumb.IsOk ? thumb.CachePath : null, w, h);
    }

    public static async Task<(BitmapImage? Image, int Width, int Height)> LoadThumbnailWithSizeAsync(string filePath, int decodeWidth = 320)
    {
        var r = await LoadThumbnailResultAsync(filePath, decodeWidth);
        return (r.Image, r.Width, r.Height);
    }

    public static async Task<BitmapImage?> LoadThumbnailAsync(string filePath, int decodeWidth = 250)
    {
        var r = await LoadThumbnailResultAsync(filePath, decodeWidth);
        return r.Image;
    }

    public static async Task<(int Width, int Height)> GetImageDimensionsAsync(string filePath)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(
                await RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(filePath)).OpenReadAsync());
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static Task DeleteToTrashAsync(string filePath)
    {
        return DeleteToTrashAsync(new[] { filePath });
    }

    public static async Task<List<(string Original, string Trash)>> DeleteToTrashAsync(IEnumerable<string> filePaths)
    {
        var moved = new List<(string Original, string Trash)>();
        foreach (var file in filePaths)
        {
            try
            {
                if (!File.Exists(file)) continue;
                var trash = Path.Combine(GetTrashDirectory(), Path.GetFileName(file));
                var metaSrc = file + ".meta.json";
                var metaDst = trash + ".meta.json";
                if (!File.Exists(metaDst) && File.Exists(metaSrc))
                {
                    try { System.IO.File.Move(metaSrc, metaDst, true); } catch { }
                }
                System.IO.File.Move(file, trash, true);
                moved.Add((file, trash));

                var thumb = ThumbPathFor(file);
                if (File.Exists(thumb))
                {
                    try { System.IO.File.Delete(thumb); } catch { }
                }
                SnipThumbnailService.DeleteCachedThumbnails(file);
            }
            catch { }
        }
        return moved;
    }

    public static void UndoDelete(IEnumerable<(string Original, string Trash)> moved)
    {
        foreach (var m in moved)
        {
            try
            {
                if (!File.Exists(m.Trash)) continue;
                System.IO.File.Move(m.Trash, m.Original, true);
                var metaDst = m.Trash + ".meta.json";
                if (File.Exists(metaDst))
                {
                    try { System.IO.File.Move(metaDst, m.Original + ".meta.json", true); } catch { }
                }
            }
            catch { }
        }
    }

    public sealed record TrashEntry(string TrashPath, string OriginalPath, string Name, long Bytes, DateTime DeletedUtc);

    /// <summary>Everything currently in the bin (file + sidecar meta if present).</summary>
    public static List<TrashEntry> GetTrashEntries()
    {
        var list = new List<TrashEntry>();
        try
        {
            var trash = GetTrashDirectory();
            var gallery = GetSnipsDirectory();
            foreach (var file in Directory.EnumerateFiles(trash))
            {
                if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                long bytes = 0;
                DateTime deleted = DateTime.UtcNow;
                try
                {
                    var info = new FileInfo(file);
                    bytes = info.Length;
                    deleted = info.LastWriteTimeUtc;
                }
                catch { }
                list.Add(new TrashEntry(file, Path.Combine(gallery, Path.GetFileName(file)),
                    Path.GetFileName(file), bytes, deleted));
            }
        }
        catch { }
        list.Sort((a, b) => b.DeletedUtc.CompareTo(a.DeletedUtc));
        return list;
    }

    /// <summary>Restores trashed files (and their metas) to the gallery. Returns restored count.</summary>
    public static int RestoreFromTrash(IEnumerable<string> trashPaths)
    {
        int done = 0;
        var gallery = GetSnipsDirectory();
        foreach (var trashPath in trashPaths)
        {
            try
            {
                var full = Path.GetFullPath(trashPath);
                var dir = Path.GetFullPath(GetTrashDirectory()) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                var dest = Path.Combine(gallery, Path.GetFileName(full));
                var n = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(gallery, $"{Path.GetFileNameWithoutExtension(full)}_{n++}{Path.GetExtension(full)}");
                System.IO.File.Move(full, dest);
                var metaSrc = full + ".meta.json";
                if (File.Exists(metaSrc))
                {
                    try { System.IO.File.Move(metaSrc, dest + ".meta.json", true); } catch { }
                }
                done++;
            }
            catch { }
        }
        return done;
    }

    /// <summary>Permanently deletes trashed files (image + meta + cached thumbs). Returns count.</summary>
    public static int DeleteForever(IEnumerable<string> trashPaths)
    {
        int done = 0;
        var dir = Path.GetFullPath(GetTrashDirectory()) + Path.DirectorySeparatorChar;
        foreach (var trashPath in trashPaths)
        {
            try
            {
                var full = Path.GetFullPath(trashPath);
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) File.Delete(full);
                var meta = full + ".meta.json";
                if (File.Exists(meta)) File.Delete(meta);
                try { SnipThumbnailService.DeleteCachedThumbnails(full); } catch { }
                done++;
            }
            catch { }
        }
        return done;
    }

    /// <summary>Finds gallery images that are a single flat color (DRM blanks, black
    /// frames, empty captures) with their sizes, so the user can reclaim the space.</summary>
    public static async Task<List<(string FilePath, string Name, long Bytes)>> FindBlankSnipsAsync()
    {
        var found = new List<(string, string, long)>();
        var entries = await GetAllSnipEntriesAsync().ConfigureAwait(false);
        foreach (var e in entries)
        {
            try
            {
                var r = await SnipThumbnailService.GetAsync(e.FilePath, SnipThumbnailService.SmallTier, includePixels: true).ConfigureAwait(false);
                if (!r.IsOk || r.Bgra is null) continue;
                if (SnipThumbnailService.IsEffectivelyBlank(r.Bgra, r.Width, r.Height) ||
                    SnipGalleryQuery.IsNearlyBlank(r.Bgra))
                    found.Add((e.FilePath, e.Name, e.SizeBytes));
            }
            catch { }
        }
        return found;
    }

    /// <summary>Removes orphaned .meta.json sidecars whose image no longer exists.</summary>
    public static void CleanupOrphanedMeta()
    {
        try
        {
            var dir = GetSnipsDirectory();
            foreach (var meta in Directory.EnumerateFiles(dir, "*.meta.json"))
            {
                var image = meta.Substring(0, meta.Length - ".meta.json".Length);
                if (!File.Exists(image))
                {
                    try { System.IO.File.Delete(meta); } catch { }
                }
            }
        }
        catch { }
    }

    public static void PurgeTrash()
    {
        try
        {
            var trash = GetTrashDirectory();
            foreach (var f in Directory.EnumerateFiles(trash))
            {
                try { System.IO.File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public static void SoftDeletePermanently(IEnumerable<(string Original, string Trash)> moved)
    {
        foreach (var m in moved)
        {
            try
            {
                if (File.Exists(m.Trash)) System.IO.File.Delete(m.Trash);
                var metaDst = m.Trash + ".meta.json";
                if (File.Exists(metaDst)) System.IO.File.Delete(metaDst);
            }
            catch { }
        }
    }

    public static async Task<int> AutoDeleteOlderThanAsync(int days)
    {
        if (days <= 0) return 0;
        var entries = await GetAllSnipEntriesAsync();
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var toDelete = entries.Where(e => !e.IsFavorite && e.CreatedUtc < cutoff).Select(e => e.FilePath).ToList();
        await DeleteToTrashAsync(toDelete);
        return toDelete.Count;
    }

    public static async Task ExportZipAsync(IEnumerable<string> filePaths, string zipPath)
    {
        var tmp = zipPath + ".tmp";
        try
        {
            if (File.Exists(tmp)) System.IO.File.Delete(tmp);
            var entries = filePaths.Where(File.Exists).ToList();
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var f in entries)
                {
                    try
                    {
                        zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
                    }
                    catch { }
                }
            }
            if (File.Exists(zipPath)) System.IO.File.Delete(zipPath);
            System.IO.File.Move(tmp, zipPath);
        }
        finally
        {
            if (File.Exists(tmp)) { try { System.IO.File.Delete(tmp); } catch { } }
        }
    }

    public static Task<string> CopyFilesAsync(IEnumerable<string> filePaths, string destDir)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            var copied = new List<string>();
            foreach (var f in filePaths)
            {
                try
                {
                    var dest = Path.Combine(destDir, Path.GetFileName(f));
                    System.IO.File.Copy(f, dest, true);
                    var metaSrc = f + ".meta.json";
                    if (File.Exists(metaSrc))
                    {
                        try { System.IO.File.Copy(metaSrc, dest + ".meta.json", true); } catch { }
                    }
                    copied.Add(dest);
                }
                catch { }
            }
            return string.Join(Environment.NewLine, copied);
        });
    }

    public static Task<int> MoveFilesAsync(IEnumerable<string> filePaths, string destDir)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            int moved = 0;
            foreach (var f in filePaths)
            {
                try
                {
                    var dest = Path.Combine(destDir, Path.GetFileName(f));
                    System.IO.File.Move(f, dest, true);
                    var metaSrc = f + ".meta.json";
                    if (File.Exists(metaSrc))
                    {
                        try { System.IO.File.Move(metaSrc, dest + ".meta.json", true); } catch { }
                    }
                    var thumb = ThumbPathFor(f);
                    if (File.Exists(thumb)) { try { System.IO.File.Delete(thumb); } catch { } }
                    SnipThumbnailService.DeleteCachedThumbnails(f);
                    moved++;
                }
                catch { }
            }
            return moved;
        });
    }

    public sealed record SnipSaveResult(string Path, bool IsUniform, int Width, int Height);

    /// <summary>True when every pixel is identical (protected/DRM content captures black;
    /// warn the user, don't block). Implemented in SnipGalleryQuery (pure, unit-tested).</summary>
    public static bool IsUniformImage(byte[] bgra) => SnipGalleryQuery.IsUniformImage(bgra);

    /// <summary>Quick-saves a snip straight into the Pictures gallery folder with
    /// correct metadata (type, dimensions). Returns whether the image is uniform.</summary>
    public static async Task<SnipSaveResult> SaveSnipAsync(byte[] bgra, int width, int height, string sourceApp = "")
    {
        var dir = GetSnipsDirectory();
        var baseName = SnipRegionLogic.BuildSnipFileName(DateTime.Now, ".png");
        var dest = Path.Combine(dir, baseName);
        var n = 1;
        while (File.Exists(dest)) dest = Path.Combine(dir, $"{baseName}_{n++}.png");

        bool uniform = IsUniformImage(bgra);

        // Write to a temp file, rename into place: a reader can never see a half-written snip.
        await SnipThumbnailService.WritePngAtomicAsync(dest, bgra, width, height);
        await SaveMetaAsync(dest, new SnipMeta { Type = "Region", CreatedUtc = DateTime.UtcNow, Width = width, Height = height, SourceApp = sourceApp ?? "" });

        // Thumbnails come from the in-memory rendered pixels: no re-decode, and they exist
        // before this snip can appear in the gallery/recent list.
        try { await SnipThumbnailService.GenerateFromPixelsAsync(dest, bgra, width, height); }
        catch { /* the lazy path will backfill it */ }

        return new SnipSaveResult(dest, uniform, width, height);
    }

    public static async Task<List<string>> ImportImagesAsync(IEnumerable<string> sourceFiles)
    {
        var imported = new List<string>();
        foreach (var src in sourceFiles)
        {
            try
            {
                var ext = Path.GetExtension(src).ToLowerInvariant();
                if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                var baseName = SnipRegionLogic.BuildSnipFileName(DateTime.Now, ext);
                var dest = Path.Combine(GetSnipsDirectory(), baseName);
                var n = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(GetSnipsDirectory(), $"{baseName}_{n++}{ext}");
                }
                System.IO.File.Copy(src, dest, false);
                var meta = new SnipMeta { Type = "Imported", CreatedUtc = DateTime.UtcNow };
                try
                {
                    using var fs = File.OpenRead(dest);
                    var decoder = BitmapDecoder.CreateAsync(fs.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                    meta.Width = (int)decoder.PixelWidth;
                    meta.Height = (int)decoder.PixelHeight;
                }
                catch { }
                await SaveMetaAsync(dest, meta);

                // Import trigger: thumbnails right after the copy completes.
                try { await SnipThumbnailService.EnsureThumbnailsAsync(dest); }
                catch { /* lazy backfill covers it */ }
                imported.Add(dest);
            }
            catch { }
        }
        return imported;
    }
}