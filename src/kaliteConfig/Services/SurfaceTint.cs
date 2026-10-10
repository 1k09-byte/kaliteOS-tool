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

/// <summary>
/// Pushes the backdrop tint INTO the app's surface brushes, so the wash reaches
/// the inside of the UI - cards, panels, list rows, hairlines - and not only the
/// gaps between them.
///
/// This works by recolouring the brush instances the whole app already shares,
/// rather than adding another layer over the content:
///
///  - every page paints from the same handful of theme brushes
///    (CardBackgroundFillColorDefaultBrush, LayerFillColorDefaultBrush, ...);
///  - WinUI resolves a ThemeResource once and hands the SAME brush instance to
///    every element that asks, so mutating that instance's Color re-skins every
///    card, panel and hairline already on screen, with no visual-tree stamp and
///    no per-element work;
///  - the original colour of each brush is captured the first time a tint is
///    applied, so the blend is always against the real theme value, and removing
///    the tint restores it exactly.
///
/// Alpha is deliberately left alone. These brushes are translucent overlays (a
/// card fill is ~70% opaque), and changing their alpha would change how much of
/// the material shows through them - the opposite of a tint. Only the hue moves.
/// </summary>
internal static class SurfaceTint
{
    /// <summary>Every surface brush the app paints from.</summary>
    private static readonly string[] Keys =
    {
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
        "CardBackgroundFillColorTertiaryBrush",
        "LayerFillColorDefaultBrush",
        "SubtleFillColorSecondaryBrush",
        "SubtleFillColorTertiaryBrush",
        "CardStrokeColorDefaultBrush",
        "ControlStrokeColorDefaultBrush",
        "SurfaceStrokeColorDefaultBrush",
        "ApplicationPageBackgroundThemeBrush",
    };

    /// <summary>The theme value of each brush, captured before any tinting.</summary>
    private static readonly Dictionary<string, Color> Base = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies the tint to every surface brush. Strength 0 (or no tint) restores
    /// the theme values exactly as they were.
    /// </summary>
    public static void Apply((byte R, byte G, byte B, byte A) tint)
    {
        double k = tint.A / (double)BackdropTint.MaxAlpha;
        if (k <= 0)
        {
            Reset();
            return;
        }

        foreach (var pair in Brushes())
        {
            Color theme = pair.Value.Color;
            if (!Base.TryGetValue(pair.Key, out Color captured))
            {
                Base[pair.Key] = theme;
                captured = theme;
            }

            pair.Value.Color = Color.FromArgb(
                captured.A,
                Mix(captured.R, tint.R, k),
                Mix(captured.G, tint.G, k),
                Mix(captured.B, tint.B, k));
        }
    }

    /// <summary>
    /// Forgets the captured theme colours so the next Apply measures the brushes
    /// as they are now. Used after a theme switch: the theme rewrites the shared
    /// brushes with its own values, and blending against a stale (already tinted)
    /// base would compound the tint on every switch.
    ///
    /// The brushes in Application.Current.Resources are already mutated by every
    /// prior Apply, so we must first restore each captured brush to its captured
    /// theme colour before forgetting the capture - otherwise the next Apply would
    /// capture an already-tinted colour as its new "theme" base and blend the tint
    /// on top of itself again.
    /// </summary>
    public static void ResetCapture()
    {
        // Restore every captured brush to the theme value we captured the first
        // time we saw it, THEN forget the captures. That leaves the shared brushes
        // at their real theme values for the next Apply.
        Reset();
        Base.Clear();
    }
    /// <summary>Restores every captured brush to its theme colour.</summary>
    public static void Reset()
    {
        if (Base.Count == 0) return;
        foreach (var pair in Brushes())
        {
            if (Base.TryGetValue(pair.Key, out Color theme))
                pair.Value.Color = theme;
        }
    }

    private static IEnumerable<KeyValuePair<string, SolidColorBrush>> Brushes()
    {
        var resources = Application.Current.Resources;
        if (resources is null) yield break;

        foreach (var key in Keys)
        {
            // Note: no pattern variable here. A is SolidColorBrush brush declared
            // inside an iterator's condition is not considered definitely assigned
            // at the yield (CS0165), so the value is taken through a local.
            if (!resources.TryGetValue(key, out object? raw)) continue;
            if (raw is not SolidColorBrush) continue;

            var brush = (SolidColorBrush)raw;
            yield return new KeyValuePair<string, SolidColorBrush>(key, brush);
        }
    }
    private static byte Mix(byte theme, byte tint, double k)
        => (byte)Math.Round(theme + (tint - theme) * Math.Clamp(k, 0, 1));
}
