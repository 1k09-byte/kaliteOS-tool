using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Models;
using Windows.Graphics.Imaging;

namespace kaliteConfig.Services;

public sealed record SnipPreviewPlaceholder(byte[] Bgra, int Width, int Height, int Tier, bool LooksBlank);

public sealed record SnipPreviewFullPixels(byte[] Bgra, int Width, int Height);

public static class SnipPreviewService
{
    public static async Task<SnipPreviewPlaceholder?> LoadPlaceholderAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var tier = SnipThumbnailService.PickTier(SnipPreviewPolicy.PlaceholderTier);
        var result = await SnipThumbnailService.GetAsync(filePath, tier, includePixels: true, ct).ConfigureAwait(true);
        if (!result.IsOk || result.Bgra is null || result.Width <= 0 || result.Height <= 0) return null;

        return new SnipPreviewPlaceholder(result.Bgra, result.Width, result.Height, result.Tier, result.LooksBlank);
    }

    public static async Task<(int Width, int Height)> ReadSizeAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return (0, 0);
        try
        {
            var bytes = await SnipThumbnailService.ReadAllBytesWithRetryAsync(filePath).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            return (OrientedWidth(decoder), OrientedHeight(decoder));
        }
        catch (OperationCanceledException) { throw; }
        catch { return (0, 0); }
    }

    public static async Task<SnipPreviewFullPixels?> LoadFullPixelsAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        var bytes = await SnipThumbnailService.ReadAllBytesWithRetryAsync(filePath).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream(bytes, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        ct.ThrowIfCancellationRequested();

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = data.DetachPixelData();
        var count = pixels.Length / 4;

        int width = OrientedWidth(decoder);
        int height = OrientedHeight(decoder);
        if ((long)width * height != count && width > 0)
        {
            height = Math.Max(1, (int)Math.Round(count / (double)width));
        }
        if (width <= 0 || height <= 0 || (long)width * height != count) return null;

        return new SnipPreviewFullPixels(pixels, width, height);
    }

    public static bool Exists(string? filePath) => !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);

    private static int OrientedWidth(BitmapDecoder decoder)
    {
        var oriented = (int)decoder.OrientedPixelWidth;
        return oriented > 0 ? oriented : (int)decoder.PixelWidth;
    }

    private static int OrientedHeight(BitmapDecoder decoder)
    {
        var oriented = (int)decoder.OrientedPixelHeight;
        return oriented > 0 ? oriented : (int)decoder.PixelHeight;
    }
}
