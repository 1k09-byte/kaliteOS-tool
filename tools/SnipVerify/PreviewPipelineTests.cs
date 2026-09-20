using System;
using System.IO;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Services;
using Windows.Graphics.Imaging;

// Phase 3 (large preview) verification: the pure zoom/pan policy, plus a REAL disk round-trip of
// the progressive load (512 px placeholder tier -> full-resolution pixels) that the details panel
// and the viewer both use. The Win2D half (CanvasVirtualBitmap regions, crossfade, interpolation)
// needs a GPU + window and is covered by the manual walkthrough in Docs/SnipPreview.md.
internal static class PreviewPipelineTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // ---- pure viewport math (mirrors kaliteconfig.Tests/SnipPreviewViewportTests) ----
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var viewport = Build(3000, 2000, 800, 600, scale);
            viewport.ActualSize();
            check(Math.Abs(viewport.DisplayWidthDip * scale - 3000) < 0.01,
                $"viewport: 100% is 1 image px per physical px at {scale * 100:0}% display scale");
        }

        var fit = Build(4000, 2000, 800, 600, 1.0);
        fit.Fit();
        check(fit.IsFullyVisible && fit.Zoom < 1.0, "viewport: fit shows the whole image");

        var small = Build(64, 48, 1200, 800, 1.0);
        small.Fit();
        check(Math.Abs(small.Zoom - 1.0) < 1e-9, "viewport: fit never magnifies a small image");

        var zoomed = Build(4000, 2000, 800, 600, 1.0);
        zoomed.ActualSize();
        zoomed.ZoomAt(400, 300, 4.0);
        var (sx, sy, sw, sh) = zoomed.VisibleSourceRectPx();
        check(sw < 4000 && sh < 2000 && sx >= 0 && sy >= 0 && sx + sw <= 4000.01 && sy + sh <= 2000.01,
            "viewport: visible source rect is a subset of the image (what gets realized)");

        check(SnipPreviewPolicy.ChooseSource(8000, 8000) == SnipPreviewSource.VirtualBitmap,
            "policy: 64 Mpx goes through CanvasVirtualBitmap");
        check(SnipPreviewPolicy.ChooseSource(1920, 1080) == SnipPreviewSource.FullBitmap,
            "policy: a normal snip decodes into one bitmap");
        check(SnipPreviewPolicy.PickInterpolation(1.0) == SnipPreviewInterpolation.Linear
            && SnipPreviewPolicy.PickInterpolation(4.0) == SnipPreviewInterpolation.NearestNeighbor
            && SnipPreviewPolicy.PickInterpolation(0.2) == SnipPreviewInterpolation.MultiSampleLinear,
            "policy: interpolation follows the zoom level");

        // ---- real disk pipeline: placeholder tier, then full-resolution pixels ----
        string? bigPath = null;
        string? smallPath = null;
        try
        {
            bigPath = Path.Combine(Path.GetTempPath(), $"snipverify_preview_{Guid.NewGuid():N}.png");
            await WriteTestPngAsync(bigPath, 1200, 900);
            check(File.Exists(bigPath), "preview IO: 1200x900 test PNG created");

            var (width, height) = await SnipPreviewService.ReadSizeAsync(bigPath);
            check(width == 1200 && height == 900, $"preview IO: header size read back ({width}x{height})");

            check(SnipPreviewPolicy.ChooseSource(width, height) == SnipPreviewSource.FullBitmap,
                "preview IO: 1200x900 uses the direct path");

            var placeholder = await SnipPreviewService.LoadPlaceholderAsync(bigPath);
            check(placeholder is not null, "preview IO: 512 placeholder tier loaded");
            if (placeholder is not null)
            {
                check(placeholder.Tier == SnipPreviewPolicy.PlaceholderTier,
                    $"preview IO: placeholder came from the {SnipPreviewPolicy.PlaceholderTier} px tier");
                check(Math.Max(placeholder.Width, placeholder.Height) == SnipPreviewPolicy.PlaceholderTier,
                    $"preview IO: placeholder is downscaled to {placeholder.Width}x{placeholder.Height}");
                check(placeholder.Bgra.Length == placeholder.Width * placeholder.Height * 4,
                    "preview IO: placeholder pixels are packed BGRA");
            }

            var full = await SnipPreviewService.LoadFullPixelsAsync(bigPath);
            check(full is not null && full.Width == 1200 && full.Height == 900, "preview IO: full-resolution pixels decoded");
            if (full is not null)
            {
                check(full.Bgra.Length == 1200 * 900 * 4,
                    $"preview IO: full pixels are 1200x900x4 ({full.Bgra.Length} bytes)");
            }

            // A snip already in the gallery must come back without a second decode (tier cache).
            var again = await SnipPreviewService.LoadPlaceholderAsync(bigPath);
            check(again is not null && again.Width == placeholder!.Width,
                "preview IO: placeholder re-served from the tier cache");

            // Small images are never upscaled into a tier.
            smallPath = Path.Combine(Path.GetTempPath(), $"snipverify_preview_small_{Guid.NewGuid():N}.png");
            await WriteTestPngAsync(smallPath, 64, 48);
            var smallPlaceholder = await SnipPreviewService.LoadPlaceholderAsync(smallPath);
            check(smallPlaceholder is not null && smallPlaceholder.Width == 64 && smallPlaceholder.Height == 48,
                "preview IO: a small snip is not upscaled by the placeholder pass");
        }
        catch (Exception ex)
        {
            check(false, "preview IO round-trip threw: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            foreach (var path in new[] { bigPath, smallPath })
            {
                try
                {
                    if (path is not null && File.Exists(path)) File.Delete(path);
                    if (path is not null) SnipThumbnailService.DeleteCachedThumbnails(path);
                }
                catch { }
            }
        }
    }

    private static SnipPreviewViewport Build(int imageWidth, int imageHeight, double viewportWidthDip,
        double viewportHeightDip, double scale)
    {
        var viewport = new SnipPreviewViewport();
        viewport.SetRasterizationScale(scale);
        viewport.SetImage(imageWidth, imageHeight);
        viewport.SetViewport(viewportWidthDip, viewportHeightDip);
        return viewport;
    }

    private static async Task WriteTestPngAsync(string path, int width, int height)
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
        using var stream = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
}
