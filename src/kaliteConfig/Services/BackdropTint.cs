// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.IO;

namespace kaliteConfig.Services;

/// <summary>
/// The user's backdrop tint: a colour and a strength, persisted next to the
/// font choice.
///
/// This is deliberately NOT part of the material itself. The material (None /
/// Acrylic / Mica, owned by DevWinUI's ThemeService) stays exactly as the user
/// picked it - the tint is a translucent wash painted over the window surface
/// *behind* the page content, so the material still renders through it and the
/// chrome (nav rail, caption band, which stay opaque) is untouched. That is
/// what makes it a tint rather than a background: at 0% nothing is visible, at
/// the maximum the material is still showing.
///
/// Store format: one line, "#RRGGBB|AA". Everything about reading it is
/// best-effort - a malformed or unreadable file falls back to "no tint"
/// rather than costing the user their appearance.
/// </summary>
internal static class BackdropTint
{
    /// <summary>
    /// Ceiling for the strength slider, in alpha units (160/255 ~ 63%). The tint
    /// must never become an opaque background: above this the material stops
    /// reading as a material and the app turns into a flat colour wash.
    /// </summary>
    public const byte MaxAlpha = 160;

    /// <summary>No tint at all: fully transparent, so nothing changes until asked for.</summary>
    public static readonly (byte R, byte G, byte B, byte A) None = (0x00, 0x00, 0x00, 0x00);

    /// <summary>
    /// The app's matte chrome surface - the nav rail and the caption band paint
    /// this, so the rail reads as part of the tool instead of a black bar.
    ///
    /// The colour is defined once in the theme (MainWindow.xaml's
    /// NavigationViewBackground), so this reads it back rather than carrying a
    /// separate hard-coded copy. When the theme resource is missing for any
    /// reason, fall back to the documented matte colour.
    /// </summary>
    public static (byte R, byte G, byte B) Matte
    {
        get
        {
            try
            {
                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(
                    "NavigationViewBackground", out var obj) &&
                    obj is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                    return (brush.Color.R, brush.Color.G, brush.Color.B);
            }
            catch { }
            return (0x10, 0x15, 0x0F);
        }
    }

    /// <summary>
    /// Blends the tint over an opaque surface, the way the page area gets its
    /// wash: the result is still opaque (so the meteor backdrop can never smear
    /// back through the chrome) but carries the tint at the user's strength.
    /// This is how the tint reaches the side rail and the title bar.
    /// </summary>
    public static (byte R, byte G, byte B) Blended((byte R, byte G, byte B) baseColor, (byte R, byte G, byte B, byte A) tint)
    {
        double k = tint.A / 255.0;
        return (
            (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * k),
            (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * k),
            (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * k));
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "backdrop-tint.txt");

    /// <summary>The stored tint, or <see cref="None"/> when nothing valid is stored.</summary>
    public static (byte R, byte G, byte B, byte A) Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return None;

            string text = File.ReadAllText(StorePath).Trim();
            if (text.Length == 0) return None;

            string[] parts = text.Split('|');
            if (parts.Length != 2) return None;

            if (!TryParseHexColor(parts[0], out byte r, out byte g, out byte b)) return None;

            // "strength" is stored as 0-100 on purpose: what a user typed in a
            // settings box, not what the brush needs, so the clamp to MaxAlpha
            // is applied on read rather than trusted from the file.
            int strength = int.TryParse(parts[1], out int s) ? s : 0;
            byte alpha = (byte)Math.Clamp(Math.Round(strength / 100.0 * MaxAlpha), 0, MaxAlpha);
            return (r, g, b, alpha);
        }
        catch
        {
            // An unreadable preference must never stop the window from opening.
            return None;
        }
    }

    /// <summary>Persists a colour and a strength (0-100). Strength 0 = no tint.</summary>
    public static void Save(byte r, byte g, byte b, int strengthPercent)
    {
        try
        {
            int clamped = (int)Math.Clamp(strengthPercent, 0, 100);
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#{r:X2}{g:X2}{b:X2}|{clamped}");
        }
        catch { /* the tint still applies for this session */ }
    }

    /// <summary>Removes the tint entirely (keeps no state behind it).</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
        }
        catch { }
    }

    /// <summary>Strength (0-100) that a stored tint corresponds to, for the settings slider.</summary>
    public static int StrengthPercentFor((byte R, byte G, byte B, byte A) tint)
        => MaxAlpha == 0 ? 0 : (int)Math.Round(tint.A / (double)MaxAlpha * 100);

    private static bool TryParseHexColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
            byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
            byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
            return true;

        // 8 digits (#AARRGGBB) is accepted and the alpha ignored: strength owns alpha.
        if (s.Length == 8 &&
            byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
            byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
            byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
            return true;

        return false;
    }
}
