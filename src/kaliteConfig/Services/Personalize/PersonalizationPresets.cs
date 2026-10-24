using System;
using System.Globalization;

namespace kaliteConfig.Services.Personalize;

public static class PersonalizationPresets
{
    public sealed record AccentSwatch(string ResourceKey, string Hex);

    public static readonly AccentSwatch[] AccentSwatches =
    {
        new("Personalize_Accent_Navy", "#19235A"),
        new("Personalize_Accent_Blue", "#0078D4"),
        new("Personalize_Accent_Teal", "#008272"),
        new("Personalize_Accent_Green", "#107C10"),
        new("Personalize_Accent_Purple", "#5C2D91"),
        new("Personalize_Accent_Red", "#E81123"),
        new("Personalize_Accent_Orange", "#CA5010"),
        new("Personalize_Accent_Pink", "#E3008C"),
        new("Personalize_Accent_Gray", "#767676"),
    };

    public static int ToAbgr(string hex, byte alpha)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7 || hex[0] != '#')
            throw new ArgumentException("Expected a color in #RRGGBB form.", nameof(hex));
        byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint value = ((uint)alpha << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        return unchecked((int)value);
    }
}
