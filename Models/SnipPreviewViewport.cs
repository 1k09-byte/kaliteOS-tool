using System;

namespace kaliteConfig.Models;

/// <summary>How the large preview samples the image at a given zoom (mapped 1:1 to Win2D's
/// CanvasImageInterpolation by the control, so the policy stays pure and testable).</summary>
public enum SnipPreviewInterpolation
{
    /// <summary>Deep minification: box-filter style sampling that cannot alias.</summary>
    MultiSampleLinear,
    /// <summary>Mild minification: slightly sharper than a box filter.</summary>
    Cubic,
    /// <summary>Around 1:1: a single texel maps to about one device pixel.</summary>
    Linear,
    /// <summary>Magnified well past 1:1: show the actual pixels, not a blur.</summary>
    NearestNeighbor,
}

/// <summary>Which kind of backing source the preview should build for an image.</summary>
public enum SnipPreviewSource
{
    /// <summary>No image (nothing to load).</summary>
    None,
    /// <summary>Decoded pixels uploaded as one CanvasBitmap.</summary>
    FullBitmap,
    /// <summary>Loaded on demand through CanvasVirtualBitmap (huge images: never fully resident).</summary>
    VirtualBitmap,
}

/// <summary>
/// Sizing / interpolation / backing-source policy for the large Snip preview.
/// Pure policy: no WinUI, no Win2D, unit-tested directly.
/// </summary>
public static class SnipPreviewPolicy
{
    /// <summary>Tier shown immediately while the full-resolution pass runs (longest edge, px).
    /// Must be one of SnipThumbnailService.Tiers (256/512/1024); 512 is the middle tier.</summary>
    public const int PlaceholderTier = 512;

    /// <summary>Above either limit the image is drawn through CanvasVirtualBitmap instead of a
    /// one-shot decode + upload. 8192 px / 32 Mpx keeps the direct path under ~128 MB.</summary>
    public const int MaxDirectEdge = 8192;
    public const long MaxDirectPixels = 32L * 1024 * 1024;

    /// <summary>Zoom bounds for the preview (1.0 = 100%).</summary>
    public const double MinZoom = 0.02;
    public const double MaxZoom = 32.0;

    /// <summary>Choose the backing source for an image of the given pixel size.</summary>
    public static SnipPreviewSource ChooseSource(int width, int height)
    {
        if (width <= 0 || height <= 0) return SnipPreviewSource.None;
        return NeedsVirtualBitmap(width, height) ? SnipPreviewSource.VirtualBitmap : SnipPreviewSource.FullBitmap;
    }

    /// <summary>True when an image is too large to hold fully decoded (memory), so regions are
    /// realized on demand instead.</summary>
    public static bool NeedsVirtualBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        return Math.Max(width, height) > MaxDirectEdge || (long)width * height > MaxDirectPixels;
    }

    /// <summary>
    /// Interpolation for a zoom level, where 1.0 = one image pixel per physical device pixel.
    /// Minified views are averaged (never aliased); magnified views stay crisp.
    /// </summary>
    public static SnipPreviewInterpolation PickInterpolation(double zoom)
    {
        if (zoom >= 2.0) return SnipPreviewInterpolation.NearestNeighbor; // pixel peeping
        if (zoom >= 0.9) return SnipPreviewInterpolation.Linear;          // about 1:1
        if (zoom >= 0.5) return SnipPreviewInterpolation.Cubic;           // slightly minified
        return SnipPreviewInterpolation.MultiSampleLinear;                // heavily minified
    }

    /// <summary>
    /// Interpolation for the 512 px placeholder. It is almost always upscaled, and nearest
    /// neighbour on an upscaled placeholder would look like a broken render, so it never uses it.
    /// </summary>
    public static SnipPreviewInterpolation PlaceholderInterpolation(double zoom) =>
        zoom >= 0.9
            ? SnipPreviewInterpolation.Linear
            : PickInterpolation(zoom);

    /// <summary>Clamps a zoom factor into the supported range.</summary>
    public static double ClampZoom(double zoom)
    {
        if (double.IsNaN(zoom) || zoom <= 0) return 1.0;
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    /// <summary>"100%", "25%", "1200%" — the label under the zoom chrome.</summary>
    public static string FormatZoom(double zoom) => $"{Math.Round(ClampZoom(zoom) * 100.0)}%";
}

/// <summary>
/// Pure zoom / pan math for the large Snip preview (details panel and full viewer).
///
/// The contract that makes 100% DPI-correct: <see cref="Zoom"/> 1.0 means one IMAGE pixel per
/// PHYSICAL device pixel, at any monitor scale. Everything is stored in DIPs (what XAML and
/// Win2D's CanvasControl speak), and <see cref="RasterizationScale"/> converts to device pixels.
/// So on a 150% monitor a 4000 px wide snip at 100% is 2666.7 DIPs wide — exactly 4000 device
/// pixels, pixel for pixel, and never a blurry "100%" that is really 100% of DIPs.
/// </summary>
public sealed class SnipPreviewViewport
{
    /// <summary>Image size in pixels.</summary>
    public int ImagePixelWidth { get; private set; }
    public int ImagePixelHeight { get; private set; }

    /// <summary>Viewport (CanvasControl) size in DIPs.</summary>
    public double ViewportWidthDip { get; private set; }
    public double ViewportHeightDip { get; private set; }

    /// <summary>XamlRoot.RasterizationScale: physical pixels per DIP.</summary>
    public double RasterizationScale { get; private set; } = 1.0;

    /// <summary>Device pixels per image pixel. 1.0 = 100%.</summary>
    public double Zoom { get; private set; } = 1.0;

    /// <summary>Top-left corner of the image, in viewport DIPs (negative when panned).</summary>
    public double OffsetXDip { get; private set; }
    public double OffsetYDip { get; private set; }

    public bool HasImage => ImagePixelWidth > 0 && ImagePixelHeight > 0;
    public bool HasViewport => ViewportWidthDip > 0 && ViewportHeightDip > 0;

    /// <summary>Image width on screen, in DIPs.</summary>
    public double DisplayWidthDip => HasImage ? ImagePixelWidth / EffectiveScale * Zoom : 0;
    public double DisplayHeightDip => HasImage ? ImagePixelHeight / EffectiveScale * Zoom : 0;

    /// <summary>True when the image is drawn at exactly one pixel per device pixel.</summary>
    public bool IsPixelPerfect => Math.Abs(Zoom - 1.0) < 0.005;

    /// <summary>True when the whole image fits inside the viewport at the current zoom/pan.</summary>
    public bool IsFullyVisible => HasImage && HasViewport
        && DisplayWidthDip <= ViewportWidthDip + 0.5 && DisplayHeightDip <= ViewportHeightDip + 0.5;

    /// <summary>Images smaller than the viewport are shown 1:1 by Fit (never upscaled).</summary>
    public const double FitZoomCap = 1.0;

    private double EffectiveScale => RasterizationScale <= 0 || double.IsNaN(RasterizationScale) ? 1.0 : RasterizationScale;

    public void SetImage(int pixelWidth, int pixelHeight)
    {
        ImagePixelWidth = Math.Max(0, pixelWidth);
        ImagePixelHeight = Math.Max(0, pixelHeight);
        ClampOffsets();
    }

    public void SetViewport(double widthDip, double heightDip)
    {
        ViewportWidthDip = double.IsNaN(widthDip) || widthDip < 0 ? 0 : widthDip;
        ViewportHeightDip = double.IsNaN(heightDip) || heightDip < 0 ? 0 : heightDip;
        ClampOffsets();
    }

    public void SetRasterizationScale(double scale)
    {
        var next = scale <= 0 || double.IsNaN(scale) ? 1.0 : scale;
        if (Math.Abs(next - RasterizationScale) < 0.0001) return;
        RasterizationScale = next;
        ClampOffsets();
    }

    /// <summary>Zoom that shows the whole image ("Fit"), never magnifying past 100%.</summary>
    public double FitZoom()
    {
        if (!HasImage || !HasViewport) return 1.0;
        var zoom = Math.Min(
            ViewportWidthDip * EffectiveScale / ImagePixelWidth,
            ViewportHeightDip * EffectiveScale / ImagePixelHeight);
        return SnipPreviewPolicy.ClampZoom(Math.Min(FitZoomCap, zoom));
    }

    /// <summary>Fit the image and center it.</summary>
    public void Fit() => SetZoomCentered(FitZoom());

    /// <summary>100%: one image pixel per physical device pixel, centered.</summary>
    public void ActualSize() => SetZoomCentered(1.0);

    /// <summary>Sets the zoom around the viewport center and re-centers the image.</summary>
    public void SetZoomCentered(double zoom)
    {
        var target = SnipPreviewPolicy.ClampZoom(zoom);
        if (Math.Abs(target - Zoom) < 1e-9)
        {
            // Already at that zoom: still center, so Fit / 100% behave like a reset.
            CenterImage();
            return;
        }
        ZoomAt(ViewportWidthDip / 2, ViewportHeightDip / 2, target / Zoom);
    }

    private void CenterImage()
    {
        OffsetXDip = (ViewportWidthDip - DisplayWidthDip) / 2;
        OffsetYDip = (ViewportHeightDip - DisplayHeightDip) / 2;
        ClampOffsets();
    }

    /// <summary>
    /// Multiplies the zoom while keeping the image point under (<paramref name="anchorXDip"/>,
    /// <paramref name="anchorYDip"/>) pinned to that spot — the standard wheel-zoom feel.
    /// </summary>
    public void ZoomAt(double anchorXDip, double anchorYDip, double factor)
    {
        if (!HasImage) return;
        var target = SnipPreviewPolicy.ClampZoom(Zoom * factor);
        if (Math.Abs(target - Zoom) < 1e-9) return;

        var (imageX, imageY) = ViewportToImagePx(anchorXDip, anchorYDip);
        Zoom = target;
        OffsetXDip = anchorXDip - imageX * Zoom / EffectiveScale;
        OffsetYDip = anchorYDip - imageY * Zoom / EffectiveScale;
        ClampOffsets();
    }

    /// <summary>Drags the image by a viewport-space delta (DIPs).</summary>
    public void PanBy(double deltaXDip, double deltaYDip)
    {
        OffsetXDip += deltaXDip;
        OffsetYDip += deltaYDip;
        ClampOffsets();
    }

    /// <summary>
    /// Keeps the pan sane: an image that fits is centered, a larger one can never be dragged
    /// fully out of view.
    /// </summary>
    public void ClampOffsets()
    {
        if (!HasImage || !HasViewport)
        {
            OffsetXDip = 0;
            OffsetYDip = 0;
            return;
        }

        var displayWidth = DisplayWidthDip;
        var displayHeight = DisplayHeightDip;

        OffsetXDip = displayWidth <= ViewportWidthDip
            ? (ViewportWidthDip - displayWidth) / 2
            : Math.Clamp(OffsetXDip, ViewportWidthDip - displayWidth, 0);

        OffsetYDip = displayHeight <= ViewportHeightDip
            ? (ViewportHeightDip - displayHeight) / 2
            : Math.Clamp(OffsetYDip, ViewportHeightDip - displayHeight, 0);
    }

    /// <summary>Image pixel coordinates of a viewport point (DIPs).</summary>
    public (double X, double Y) ViewportToImagePx(double viewportXDip, double viewportYDip)
    {
        var scale = EffectiveScale;
        return (
            (viewportXDip - OffsetXDip) * scale / Zoom,
            (viewportYDip - OffsetYDip) * scale / Zoom);
    }

    /// <summary>Viewport coordinates (DIPs) of an image pixel.</summary>
    public (double X, double Y) ImageToViewportDip(double imageX, double imageY)
    {
        var scale = EffectiveScale;
        return (OffsetXDip + imageX * Zoom / scale, OffsetYDip + imageY * Zoom / scale);
    }

    /// <summary>The destination rectangle (DIPs) for a source rectangle given in image pixels.</summary>
    public (double X, double Y, double Width, double Height) ImageRectToViewportDip(
        double imageX, double imageY, double imageWidth, double imageHeight)
    {
        var (x0, y0) = ImageToViewportDip(imageX, imageY);
        var (x1, y1) = ImageToViewportDip(imageX + imageWidth, imageY + imageHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The part of the image that is actually on screen, in image pixels, clamped to the image.
    /// This is the source rectangle handed to CanvasVirtualBitmap: only the regions behind it are
    /// realized, which is what keeps a 20000 x 20000 snip from ever being fully resident.
    /// </summary>
    public (double X, double Y, double Width, double Height) VisibleSourceRectPx()
    {
        if (!HasImage) return (0, 0, 0, 0);
        if (!HasViewport) return (0, 0, ImagePixelWidth, ImagePixelHeight);

        var (x0, y0) = ViewportToImagePx(0, 0);
        var (x1, y1) = ViewportToImagePx(ViewportWidthDip, ViewportHeightDip);

        x0 = Math.Clamp(x0, 0, ImagePixelWidth);
        y0 = Math.Clamp(y0, 0, ImagePixelHeight);
        x1 = Math.Clamp(x1, 0, ImagePixelWidth);
        y1 = Math.Clamp(y1, 0, ImagePixelHeight);

        if (x1 <= x0 || y1 <= y0) return (0, 0, ImagePixelWidth, ImagePixelHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Human-readable zoom label ("Fit" is the control's business, this is the %).</summary>
    public string ZoomLabel => SnipPreviewPolicy.FormatZoom(Zoom);

    /// <summary>Zoom as a percentage: 100.0 at actual size.</summary>
    public double ZoomPercent => Zoom * 100.0;

    /// <summary>Interpolation for the current zoom.</summary>
    public SnipPreviewInterpolation Interpolation => SnipPreviewPolicy.PickInterpolation(Zoom);

    /// <summary>0..1 with 1 == at the top-left edge (used for the overview/scroll hints).</summary>
    public double PanProgressX => DisplayWidthDip <= ViewportWidthDip
        ? 0.5
        : -OffsetXDip / Math.Max(1, DisplayWidthDip - ViewportWidthDip);

    public double PanProgressY => DisplayHeightDip <= ViewportHeightDip
        ? 0.5
        : -OffsetYDip / Math.Max(1, DisplayHeightDip - ViewportHeightDip);
}
