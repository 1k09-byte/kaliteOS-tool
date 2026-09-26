// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace kaliteConfig.Models;

/// <summary>
/// One legal choice of a setup item, as written in the dump: the raw token
/// that must be written back to the file ("0x28", "Enable") plus an optional
/// human label ("40").
/// </summary>
public sealed record BiosOption(string RawToken, string? Label)
{
    public string Display => Label is null ? RawToken : $"{Label} ({RawToken})";
}

/// <summary>Inclusive numeric range for items whose Options express "low .. high".</summary>
public sealed record BiosNumericRange(long Low, long High, string Raw)
{
    public bool Contains(long value) => value >= Low && value <= High;
}

/// <summary>
/// A single parsed setup item (one "Setup Question" block).
///
/// All parsed fields are immutable; only <see cref="Value"/> is editable.
/// The raw block text is kept verbatim (including line endings) so the
/// exporter can round-trip a file byte-for-byte and rewrite only the
/// "Value" lines of edited items.
/// </summary>
public sealed class BiosSetting
{
    public required string SetupQuestion { get; init; }
    public string? HelpString { get; init; }
    public string? Token { get; init; }
    public string? Offset { get; init; }
    public string? Width { get; init; }
    public string? BiosDefault { get; init; }
    public string? OptionsRaw { get; init; }

    /// <summary>Enumerated choices, or null when the item has none.</summary>
    public IReadOnlyList<BiosOption>? Options { get; init; }

    /// <summary>Inclusive numeric range when Options was a "low .. high" range.</summary>
    public BiosNumericRange? Range { get; init; }

    /// <summary>The value as it appeared in the imported file.</summary>
    public required string OriginalValue { get; init; }

    /// <summary>Menu breadcrumb, e.g. ["Advanced", "CPU Configuration"].</summary>
    public required string[] MenuPath { get; init; }

    /// <summary>Verbatim item text (all lines of the block, with original line endings).</summary>
    public required string RawBlock { get; init; }

    /// <summary>0-based index of the "Value = ..." line inside <see cref="RawBlock"/>, or -1 when the dump has no Value line (starred-option script style).</summary>
    public required int ValueLineIndex { get; init; }

    /// <summary>True when the dump carries an explicit "Value =" line for this item.</summary>
    public bool HasValueLine => ValueLineIndex >= 0;

    /// <summary>Leaf section this item lives under (set by the parser).</summary>
    public BiosMenuSection? Section { get; internal set; }

    /// <summary>Current (editable) value; written to the "Value" line on export.</summary>
    public string Value { get; set; } = "";

    public bool HasEnumeratedOptions => Options is { Count: > 0 };

    public bool IsModified =>
        !string.Equals(Value.Trim(), OriginalValue.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>null when the current value is acceptable, otherwise a human-readable reason.</summary>
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

        // Free-form: if the original value was numeric, the replacement must
        // stay numeric. If the original was free text, any non-empty text is ok.
        if (TryParseNumber(OriginalValue, out _))
            return TryParseNumber(v, out _) ? null : NotANumber;
        return null;
    }

    private const string NotANumber = "Not a valid number - use 0x-prefixed hex or plain decimal.";

    /// <summary>Parses "0x2C" (hex) or "44" (decimal); tolerates a trailing annotation.</summary>
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
