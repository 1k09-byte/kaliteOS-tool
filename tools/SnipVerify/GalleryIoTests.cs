using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class GalleryIoTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string? src = null;
        string? imported = null;
        try
        {
            src = Path.Combine(Path.GetTempPath(), $"snipverify_src_{Guid.NewGuid():N}.png");
            using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 64, 48))
            using (var fs = File.Create(src))
            {
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
                enc.SetSoftwareBitmap(bmp);
                await enc.FlushAsync();
            }
            check(File.Exists(src), "IO setup: source PNG created");

            var result = await SnipGalleryService.ImportImagesAsync(new[] { src });
            check(result.Count == 1, "IO ImportImagesAsync returns 1 path");
            imported = result.FirstOrDefault();
            check(imported != null && File.Exists(imported), "IO imported PNG exists in Snips dir");
            check(imported != null && File.Exists(imported + ".meta.json"), "IO sidecar meta written");

            var entries = await SnipGalleryService.GetAllSnipEntriesAsync();
            check(entries.Any(e => e.FilePath == imported), "IO enumeration lists imported snip");

            var (thumbPath, w, h) = await SnipGalleryService.GetThumbnailPathAsync(imported!, 320);
            check(thumbPath != null && File.Exists(thumbPath), "IO thumbnail cache file generated");
            check(w == 64 && h == 48, $"IO thumbnail dims read back ({w}x{h})");

            var meta = new kaliteConfig.Models.SnipMeta
            {
                Type = "Region", CreatedUtc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
                Width = 64, Height = 48, IsFavorite = true, SourceApp = "test.exe",
                OcrText = "hello", Tags = new System.Collections.Generic.List<string> { "a", "b" },
            };
            await SnipGalleryService.SaveMetaAsync(imported!, meta);
            var back = await SnipGalleryService.LoadMetaAsync(imported!);
            check(back != null && back.IsFavorite && back.Width == 64 && back.OcrText == "hello"
                && back.Tags.Count == 2 && back.SourceApp == "test.exe", "IO meta round-trips all fields");
        }
        catch (Exception ex)
        {
            check(false, "IO round-trip threw: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                if (imported != null)
                {
                    if (File.Exists(imported)) File.Delete(imported);
                    if (File.Exists(imported + ".meta.json")) File.Delete(imported + ".meta.json");
                    var thumb = SnipGalleryService.ThumbPathFor(imported);
                    if (File.Exists(thumb)) File.Delete(thumb);
                }
                if (src != null && File.Exists(src)) File.Delete(src);
            }
            catch { }
            SnipGalleryService.CleanupOrphanedMeta();
        }
    }
}
