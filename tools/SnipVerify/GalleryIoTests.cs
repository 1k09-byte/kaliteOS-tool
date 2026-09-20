using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

// Exercises the REAL SnipGalleryService disk round-trip that the overlay's
// Save button depends on: ImportImagesAsync -> GetAllSnipEntriesAsync.
internal static class GalleryIoTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string? src = null;
        string? imported = null;
        try
        {
            // 1. Build a real PNG outside the gallery (simulates the picker's file).
            src = Path.Combine(Path.GetTempPath(), $"snipverify_src_{Guid.NewGuid():N}.png");
            using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 64, 48))
            using (var fs = File.Create(src))
            {
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
                enc.SetSoftwareBitmap(bmp);
                await enc.FlushAsync();
            }
            check(File.Exists(src), "IO setup: source PNG created");

            // 2. Import exactly like BtnSave_Click does.
            var result = await SnipGalleryService.ImportImagesAsync(new[] { src });
            check(result.Count == 1, "IO ImportImagesAsync returns 1 path");
            imported = result.FirstOrDefault();
            check(imported != null && File.Exists(imported), "IO imported PNG exists in Snips dir");
            check(imported != null && File.Exists(imported + ".meta.json"), "IO sidecar meta written");

            // 3. Gallery enumeration finds it.
            var entries = await SnipGalleryService.GetAllSnipEntriesAsync();
            check(entries.Any(e => e.FilePath == imported), "IO enumeration lists imported snip");

            // 4. Thumbnail cache generation works on the real file.
            var (thumbPath, w, h) = await SnipGalleryService.GetThumbnailPathAsync(imported!, 320);
            check(thumbPath != null && File.Exists(thumbPath), "IO thumbnail cache file generated");
            check(w == 64 && h == 48, $"IO thumbnail dims read back ({w}x{h})");
        }
        catch (Exception ex)
        {
            check(false, "IO round-trip threw: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // Leave the user's gallery exactly as we found it.
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
