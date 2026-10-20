using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace kaliteConfig.Models;

public sealed record BiosOption(string RawToken, string? Label)
{
    public string Display => Label is null ? RawToken : $"{Label} ({RawToken})";

    public string FriendlyDisplay => string.IsNullOrWhiteSpace(Label) ? RawToken : Label!;
}

public sealed record BiosNumericRange(long Low, long High, string Raw)
{
    public bool Contains(long value) => value >= Low && value <= High;
}

public sealed class BiosSetting
{
    public required string SetupQuestion { get; init; }
    public string? HelpString { get; init; }
    public string? Token { get; init; }
    public string? Offset { get; init; }
    public string? Width { get; init; }
    public string? BiosDefault { get; init; }
    public string? OptionsRaw { get; init; }

    public IReadOnlyList<BiosOption>? Options { get; init; }

    public BiosNumericRange? Range { get; init; }

    public required string OriginalValue { get; init; }

    public required string[] MenuPath { get; init; }

    public required string RawBlock { get; init; }

    public required int ValueLineIndex { get; init; }

    public bool HasValueLine => ValueLineIndex >= 0;

    public BiosMenuSection? Section { get; internal set; }

    public string Value { get; set; } = "";

    public bool HasEnumeratedOptions => Options is { Count: > 0 };

    public bool IsModified =>
        !string.Equals(Value.Trim(), OriginalValue.Trim(), StringComparison.OrdinalIgnoreCase);

    public string? ValidateValue()
    {
        var v = Value?.Trim();
        if (string.IsNullOrEmpty(v)) return "Value is empty.";

        if (HasEnumeratedOptions)
        {
            bool ok = Options!.Any(o =>
                string.Equals(o.RawToken, v, StringComparison.OrdinalIgnoreCase) ||
                (o.Label is not null && string.Equals(o.Label, v, StringComparison.OrdinalIgnoreCase)));
            return ok ? null : $"'{v}' is not in this setting's Options list.";
        }

        if (Range is { } range)
        {
            if (!TryParseNumber(v, out var n)) return NotANumber;
            return range.Contains(n) ? null : $"'{v}' is outside the allowed range ({range.Raw}).";
        }

        if (TryParseNumber(OriginalValue, out _))
            return TryParseNumber(v, out _) ? null : NotANumber;
        return null;
    }

    private const string NotANumber = "Not a valid number - use 0x-prefixed hex or plain decimal.";

    public static bool TryParseNumber(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        int sp = t.IndexOfAny(new[] { ' ', '\t' });
        if (sp > 0) t = t[..sp].TrimEnd();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("&h", StringComparison.OrdinalIgnoreCase))
        {
            var hex = t[2..];
            if (hex.Length == 0) return false;
            return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
