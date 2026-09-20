using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
                foreach (var file in Directory.EnumerateFiles(dir))
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
                foreach (var file in Directory.EnumerateFiles(dir))
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

    /// <summary>Ensures a cached thumbnail file exists. Thread-safe: creates no XAML objects.</summary>
    public static async Task<(string? Path, int Width, int Height)> GetThumbnailPathAsync(string filePath, int decodeWidth = 320)
    {
        try
        {
            var thumbPath = ThumbPathFor(filePath);
            if (!File.Exists(thumbPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                using var src = await file.OpenReadAsync();
                var original = await BitmapDecoder.CreateAsync(src);
                int w = (int)original.PixelWidth;
                int h = (int)original.PixelHeight;

                var transform = new BitmapTransform();
                double scale = Math.Min(1.0, (double)decodeWidth / Math.Max(original.PixelWidth, original.PixelHeight));
                transform.ScaledWidth = (uint)Math.Max(1, (int)(original.PixelWidth * scale));
                transform.ScaledHeight = (uint)Math.Max(1, (int)(original.PixelHeight * scale));
                transform.InterpolationMode = BitmapInterpolationMode.Fant;

                using var scaled = await original.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

                using var outStream = File.Create(thumbPath);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(scaled);
                await encoder.FlushAsync();

                if (File.Exists(filePath + ".meta.json"))
                {
                    var meta = await LoadMetaAsync(filePath);
                    if (meta != null)
                    {
                        meta.Width = w;
                        meta.Height = h;
                        await SaveMetaAsync(filePath, meta);
                    }
                }
                return (thumbPath, w, h);
            }
            else
            {
                var thumbFile = await StorageFile.GetFileFromPathAsync(thumbPath);
                using var thumbStream = await thumbFile.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(thumbStream);
                return (thumbPath, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
            }
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    public static async Task<(BitmapImage? Image, int Width, int Height)> LoadThumbnailWithSizeAsync(string filePath, int decodeWidth = 320)
    {
        try
        {
            // Must run on the UI thread: BitmapImage is a XAML object.
            var (thumbPath, w, h) = await GetThumbnailPathAsync(filePath, decodeWidth);
            var loadPath = thumbPath ?? filePath;
            var file2 = await StorageFile.GetFileFromPathAsync(loadPath);
            using var stream2 = await file2.OpenReadAsync();
            var bmp = new BitmapImage();
            bmp.DecodePixelWidth = decodeWidth;
            await bmp.SetSourceAsync(stream2);
            return (bmp, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    public static async Task<BitmapImage?> LoadThumbnailAsync(string filePath, int decodeWidth = 250)
    {
        var (img, _, _) = await LoadThumbnailWithSizeAsync(filePath, decodeWidth);
        return img;
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
    public static async Task<SnipSaveResult> SaveSnipAsync(byte[] bgra, int width, int height)
    {
        var dir = GetSnipsDirectory();
        var baseName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        var dest = Path.Combine(dir, baseName + ".png");
        var n = 1;
        while (File.Exists(dest)) dest = Path.Combine(dir, $"{baseName}_{n++}.png");

        bool uniform = IsUniformImage(bgra);
        using (var fs = File.Create(dest))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
        }
        await SaveMetaAsync(dest, new SnipMeta { Type = "Region", CreatedUtc = DateTime.UtcNow, Width = width, Height = height });
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
                var baseName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                var dest = Path.Combine(GetSnipsDirectory(), baseName + ext);
                var n = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(GetSnipsDirectory(), $"{baseName}_{n++}{ext}");
                }
                System.IO.File.Copy(src, dest);
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
                imported.Add(dest);
            }
            catch { }
        }
        return imported;
    }
}