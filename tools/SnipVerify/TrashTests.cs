using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;
using Windows.Graphics.Imaging;

// Bin round-trip and blank detection against the real service (cleaned up after).
internal static class TrashTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(SnipThumbnailService.IsEffectivelyBlank(new byte[16 * 16 * 4], 16, 16), "trash zero buffer is blank");
        var varied = new byte[16 * 16 * 4];
        varied[100] = 255;
        check(!SnipThumbnailService.IsEffectivelyBlank(varied, 16, 16), "trash varied buffer is not blank");

        string? imported = null;
        try
        {
            // Uniform black PNG: the exact thing the blank finder hunts.
            var src = Path.Combine(Path.GetTempPath(), $"snipverify_blank_{Guid.NewGuid():N}.png");
            using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 32, 32))
            using (var fs = File.Create(src))
            {
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
                enc.SetSoftwareBitmap(bmp);
                await enc.FlushAsync();
            }
            var result = await SnipGalleryService.ImportImagesAsync(new[] { src });
            try { File.Delete(src); } catch { }
            check(result.Count == 1, "trash setup: blank imported");
            imported = result.FirstOrDefault();
            if (imported is null) return;

            var blanks = await SnipGalleryService.FindBlankSnipsAsync();
            check(blanks.Any(b => b.FilePath == imported), "trash finder flags the flat image");

            var moved = await SnipGalleryService.DeleteToTrashAsync(new[] { imported });
            check(moved.Count == 1 && !File.Exists(imported), "trash delete moves file out");
            check(SnipGalleryService.GetTrashEntries().Any(t => t.TrashPath == moved[0].Trash), "trash lists the binned file");

            int restored = SnipGalleryService.RestoreFromTrash(new[] { moved[0].Trash });
            check(restored == 1 && File.Exists(imported), "trash restore brings it back");

            var moved2 = await SnipGalleryService.DeleteToTrashAsync(new[] { imported });
            int gone = SnipGalleryService.DeleteForever(moved2.Select(m => m.Trash));
            check(gone == 1 && !File.Exists(imported) && !File.Exists(imported + ".meta.json"), "trash forever removes file+meta");
            check(!SnipGalleryService.GetTrashEntries().Any(t => t.TrashPath == moved2[0].Trash), "trash unlists it");
            imported = null;
        }
        catch (Exception ex)
        {
            check(false, "trash round-trip threw: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                if (imported != null)
                {
                    if (File.Exists(imported)) File.Delete(imported);
                    if (File.Exists(imported + ".meta.json")) File.Delete(imported + ".meta.json");
                    var moved = await SnipGalleryService.DeleteToTrashAsync(new[] { imported });
                    foreach (var m in moved) SnipGalleryService.DeleteForever(new[] { m.Trash });
                }
            }
            catch { }
            SnipGalleryService.CleanupOrphanedMeta();
        }
    }
}
