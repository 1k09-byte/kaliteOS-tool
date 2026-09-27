// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;

namespace kaliteConfig.Models;

/// <summary>
/// A single supported display mode (resolution + refresh rate + orientation)
/// enumerated from <c>EnumDisplaySettingsExW</c>.
/// </summary>
public sealed class DisplayMode
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required uint RefreshRate { get; init; }
    public required int Orientation { get; init; }  // DMDO_ constant

    /// <summary>Display string for combo boxes, e.g. "2560 × 1440".</summary>
    public string ResolutionDisplay => $"{Width} × {Height}";

    /// <summary>Display string for refresh rate combos, e.g. "165 Hz".</summary>
    public string RefreshRateDisplay => $"{RefreshRate} Hz";

    /// <summary>Unique key for deduplication when grouping resolutions.</summary>
    public string ResolutionKey => $"{Width}x{Height}";

    public override string ToString() => $"{Width}×{Height} @ {RefreshRate} Hz";

    public override bool Equals(object? obj) =>
        obj is DisplayMode other &&
        Width == other.Width && Height == other.Height &&
        RefreshRate == other.RefreshRate && Orientation == other.Orientation;

    public override int GetHashCode() => HashCode.Combine(Width, Height, RefreshRate, Orientation);
}
