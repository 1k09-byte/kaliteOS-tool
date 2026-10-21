using System;
using System.IO;

namespace kaliteConfig.Services;

internal static class BackdropTint
{
    public const byte MaxAlpha = 160;

    public const byte ChromeAlpha = 0xD9;

    public static readonly (byte R, byte G, byte B, byte A) None = (0x00, 0x00, 0x00, 0x00);

    public static readonly (byte R, byte G, byte B) DefaultColor = (0x1B, 0x3A, 0x6B);

    public static (byte R, byte G, byte B) ColorOf((byte R, byte G, byte B, byte A) tint)
        => tint.A == 0 || (tint.R == 0 && tint.G == 0 && tint.B == 0) ? DefaultColor : (tint.R, tint.G, tint.B);

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
            return (0x0A, 0x0A, 0x0A);
        }
    }

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

            int strength = int.TryParse(parts[1], out int s) ? s : 0;
            byte alpha = (byte)Math.Clamp(Math.Round(strength / 100.0 * MaxAlpha), 0, MaxAlpha);
            return (r, g, b, alpha);
        }
        catch
        {
            return None;
        }
    }

    public static void Save(byte r, byte g, byte b, int strengthPercent)
    {
        try
        {
            int clamped = (int)Math.Clamp(strengthPercent, 0, 100);
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#{r:X2}{g:X2}{b:X2}|{clamped}");
        }
        catch {  }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
        }
        catch { }
    }

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

        if (s.Length == 8 &&
            byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
            byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
            byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
            return true;

        return false;
    }
}
