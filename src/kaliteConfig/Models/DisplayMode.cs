using System;

namespace kaliteConfig.Models;

public sealed class DisplayMode
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required uint RefreshRate { get; init; }
    public required int Orientation { get; init; }

    public string ResolutionDisplay => $"{Width} × {Height}";

    public string RefreshRateDisplay => $"{RefreshRate} Hz";

    public string ResolutionKey => $"{Width}x{Height}";

    public override string ToString() => $"{Width}×{Height} @ {RefreshRate} Hz";

    public override bool Equals(object? obj) =>
        obj is DisplayMode other &&
        Width == other.Width && Height == other.Height &&
        RefreshRate == other.RefreshRate && Orientation == other.Orientation;

    public override int GetHashCode() => HashCode.Combine(Width, Height, RefreshRate, Orientation);
}
