using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using kaliteConfig.Services;
using Windows.Graphics.Imaging;

// Covers the sticker store round-trip (import -> list -> delete) plus naming rules.
internal static class StickerTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(SnipStickerService.SanitizeStickerName("cool cat.png") == "cool cat", "sticker name kept");
        check(SnipStickerService.SanitizeStickerName("a<b>c.png") == "a_b_c", "sticker name sanitized");
        check(SnipStickerService.SanitizeStickerName("   .png") == "sticker", "sticker name fallback");

        string? src = null;
        string? imported = null;
        try
        {
            src = Path.Combine(Path.GetTempPath(), $"snipverify_stk_{Guid.NewGuid():N}.png");
            using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 32, 32))
            using (var fs = File.Create(src))
            {
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
                enc.SetSoftwareBitmap(bmp);
                await enc.FlushAsync();
            }

            var result = await SnipStickerService.ImportStickersAsync(new[] { src });
            check(result.Count == 1, "sticker import returns 1 path");
            imported = result.FirstOrDefault();
            check(imported != null && File.Exists(imported), "sticker file exists in store");

            var listed = SnipStickerService.GetAllStickers();
            check(listed.Any(s => s.FilePath == imported), "sticker listed in store");

            // Same name twice must not overwrite.
            var again = await SnipStickerService.ImportStickersAsync(new[] { src });
            check(again.Count == 1 && again[0] != imported && File.Exists(imported),
                "sticker re-import dedupes instead of overwriting");
            try { if (File.Exists(again[0])) File.Delete(again[0]); } catch { }

            // Traversal guard: nothing outside the store may be deleted.
            check(!SnipStickerService.DeleteSticker(src), "sticker delete refuses outside paths");
            check(File.Exists(src), "sticker guard left the source alone");

            check(SnipStickerService.DeleteSticker(imported!), "sticker delete succeeds");
            check(!File.Exists(imported!), "sticker file gone after delete");
            check(!SnipStickerService.GetAllStickers().Any(s => s.FilePath == imported), "sticker unlisted after delete");
        }
        catch (Exception ex)
        {
            check(false, "sticker round-trip threw: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                if (imported != null && File.Exists(imported)) File.Delete(imported);
                if (src != null && File.Exists(src)) File.Delete(src);
            }
            catch { }
        }
    }
}
