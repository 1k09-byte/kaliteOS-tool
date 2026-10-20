// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace kaliteConfig.Services;

internal static class SurfaceTint
{
    private static readonly string[] Keys =
    {
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
        "CardBackgroundFillColorTertiaryBrush",
        "LayerFillColorDefaultBrush",
        "SubtleFillColorSecondaryBrush",
        "SubtleFillColorTertiaryBrush",
        "ApplicationPageBackgroundThemeBrush",
    };

    private static readonly Dictionary<string, Color> Base = new(StringComparer.Ordinal);

    private static bool? BaseLight;

    public static void Apply((byte R, byte G, byte B, byte A) tint, bool light)
    {
        double k = tint.A / (double)BackdropTint.MaxAlpha;
        if (k <= 0)
        {
            Reset(light);
            return;
        }

        foreach (var pair in Brushes(light))
        {
            Color theme = pair.Value.Color;
            if (!Base.TryGetValue(pair.Key, out Color captured))
            {
                Base[pair.Key] = theme;
                captured = theme;
            }
            BaseLight = light;

            pair.Value.Color = Color.FromArgb(
                captured.A,
                Mix(captured.R, tint.R, k),
                Mix(captured.G, tint.G, k),
                Mix(captured.B, tint.B, k));
        }
    }

    public static void ResetCapture(bool light)
    {
        Reset(BaseLight ?? light);
        Base.Clear();
        BaseLight = null;
    }
    public static void Reset(bool light)
    {
        if (Base.Count == 0) return;
        foreach (var pair in Brushes(light))
        {
            if (Base.TryGetValue(pair.Key, out Color theme))
                pair.Value.Color = theme;
        }
    }

    private static IEnumerable<KeyValuePair<string, SolidColorBrush>> Brushes(bool light)
    {
        foreach (var key in Keys)
        {
            if (Theming.ThemeHelper.ThemedBrush(key, light) is SolidColorBrush brush)
                yield return new KeyValuePair<string, SolidColorBrush>(key, brush);
        }
    }
    private static byte Mix(byte theme, byte tint, double k)
        => (byte)Math.Round(theme + (tint - theme) * Math.Clamp(k, 0, 1));
}
