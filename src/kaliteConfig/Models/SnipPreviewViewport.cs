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

namespace kaliteConfig.Models;

public enum SnipPreviewInterpolation
{
    MultiSampleLinear,
    Cubic,
    Linear,
    NearestNeighbor,
}

public enum SnipPreviewSource
{
    None,
    FullBitmap,
    VirtualBitmap,
}

public static class SnipPreviewPolicy
{
    public const int PlaceholderTier = 512;

    public const int MaxDirectEdge = 8192;
    public const long MaxDirectPixels = 32L * 1024 * 1024;

    public const double MinZoom = 0.02;
    public const double MaxZoom = 32.0;

    public static SnipPreviewSource ChooseSource(int width, int height)
    {
        if (width <= 0 || height <= 0) return SnipPreviewSource.None;
        return NeedsVirtualBitmap(width, height) ? SnipPreviewSource.VirtualBitmap : SnipPreviewSource.FullBitmap;
    }

    public static bool NeedsVirtualBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        return Math.Max(width, height) > MaxDirectEdge || (long)width * height > MaxDirectPixels;
    }

    public static SnipPreviewInterpolation PickInterpolation(double zoom)
    {
        if (zoom >= 2.0) return SnipPreviewInterpolation.NearestNeighbor;
        if (zoom >= 0.9) return SnipPreviewInterpolation.Linear;
        if (zoom >= 0.5) return SnipPreviewInterpolation.Cubic;
        return SnipPreviewInterpolation.MultiSampleLinear;
    }

    public static SnipPreviewInterpolation PlaceholderInterpolation(double zoom) =>
        zoom >= 0.9
            ? SnipPreviewInterpolation.Linear
            : PickInterpolation(zoom);

    public static double ClampZoom(double zoom)
    {
        if (double.IsNaN(zoom) || zoom <= 0) return 1.0;
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    public static string FormatZoom(double zoom) => $"{Math.Round(ClampZoom(zoom) * 100.0)}%";
}

public sealed class SnipPreviewViewport
{
    public int ImagePixelWidth { get; private set; }
    public int ImagePixelHeight { get; private set; }

    public double ViewportWidthDip { get; private set; }
    public double ViewportHeightDip { get; private set; }

    public double RasterizationScale { get; private set; } = 1.0;

    public double Zoom { get; private set; } = 1.0;

    public double OffsetXDip { get; private set; }
    public double OffsetYDip { get; private set; }

    public bool HasImage => ImagePixelWidth > 0 && ImagePixelHeight > 0;
    public bool HasViewport => ViewportWidthDip > 0 && ViewportHeightDip > 0;

    public double DisplayWidthDip => HasImage ? ImagePixelWidth / EffectiveScale * Zoom : 0;
    public double DisplayHeightDip => HasImage ? ImagePixelHeight / EffectiveScale * Zoom : 0;

    public bool IsPixelPerfect => Math.Abs(Zoom - 1.0) < 0.005;

    public bool IsFullyVisible => HasImage && HasViewport
        && DisplayWidthDip <= ViewportWidthDip + 0.5 && DisplayHeightDip <= ViewportHeightDip + 0.5;

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

    public double FitZoom()
    {
        if (!HasImage || !HasViewport) return 1.0;
        var zoom = Math.Min(
            ViewportWidthDip * EffectiveScale / ImagePixelWidth,
            ViewportHeightDip * EffectiveScale / ImagePixelHeight);
        return SnipPreviewPolicy.ClampZoom(Math.Min(FitZoomCap, zoom));
    }

    public void Fit() => SetZoomCentered(FitZoom());

    public void ActualSize() => SetZoomCentered(1.0);

    public void SetZoomCentered(double zoom)
    {
        var target = SnipPreviewPolicy.ClampZoom(zoom);
        if (Math.Abs(target - Zoom) < 1e-9)
        {
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

    public void PanBy(double deltaXDip, double deltaYDip)
    {
        OffsetXDip += deltaXDip;
        OffsetYDip += deltaYDip;
        ClampOffsets();
    }

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

    public (double X, double Y) ViewportToImagePx(double viewportXDip, double viewportYDip)
    {
        var scale = EffectiveScale;
        return (
            (viewportXDip - OffsetXDip) * scale / Zoom,
            (viewportYDip - OffsetYDip) * scale / Zoom);
    }

    public (double X, double Y) ImageToViewportDip(double imageX, double imageY)
    {
        var scale = EffectiveScale;
        return (OffsetXDip + imageX * Zoom / scale, OffsetYDip + imageY * Zoom / scale);
    }

    public (double X, double Y, double Width, double Height) ImageRectToViewportDip(
        double imageX, double imageY, double imageWidth, double imageHeight)
    {
        var (x0, y0) = ImageToViewportDip(imageX, imageY);
        var (x1, y1) = ImageToViewportDip(imageX + imageWidth, imageY + imageHeight);
        return (x0, y0, x1 - x0, y1 - y0);
    }

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

    public string ZoomLabel => SnipPreviewPolicy.FormatZoom(Zoom);

    public double ZoomPercent => Zoom * 100.0;

    public SnipPreviewInterpolation Interpolation => SnipPreviewPolicy.PickInterpolation(Zoom);

    public double PanProgressX => DisplayWidthDip <= ViewportWidthDip
        ? 0.5
        : -OffsetXDip / Math.Max(1, DisplayWidthDip - ViewportWidthDip);

    public double PanProgressY => DisplayHeightDip <= ViewportHeightDip
        ? 0.5
        : -OffsetYDip / Math.Max(1, DisplayHeightDip - ViewportHeightDip);
}
