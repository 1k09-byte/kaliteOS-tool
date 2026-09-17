using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>Thrown when a file doesn't look like a SCEWIN dump at all.</summary>
public sealed class ScewinParseException : Exception
{
    public ScewinParseException(string message) : base(message) { }
}

/// <summary>
/// Parses SCEWIN / AMISCE dump text into a <see cref="ScewinDocument"/>.
///
/// Real-world grammar (AMISCE Ver 5.05, confirmed against a live dump):
///  - Records start at "Setup Question &lt;tab&gt;= ..." and run until the next
///    record or EOF; records are separated by BLANK LINES (no dashed rules).
///  - Fields: Help String / Token / Offset / Width / BIOS Default / Options /
///    Value. "Token =09   // Do NOT change this line" carries an inline //
///    comment that is NOT part of the value.
///  - Enumerated items list choices across CONTINUATION LINES, one per line:
///        Options  =*[00]Auto    // Move "*" to the desired Option
///                 [01]Manual
///    The leading "*" marks the CURRENT value. Numeric items have a
///    "Value =&lt;550&gt;" line instead (angle brackets are the value syntax).
///  - Some dumps add "----- Section ----- Sub -----"-style headers; this one
///    does not, so items with no header land in the root "All settings".
///
/// Design goals:
///  - Tolerant: splits key/value on the FIRST '=' per line and trims; CRLF/LF,
///    missing optional fields and unknown keys are all accepted.
///  - Round-trip faithful: every line lands in exactly one segment whose text
///    is preserved verbatim; the exporter only rewrites the item's value line.
///  - Pure: text in, model out — no file I/O, no UI dependencies.
/// </summary>
public static class ScewinParser
{
    private const string KSetupQuestion = "setupquestion";
    private const string KHelpString = "helpstring";
    private const string KToken = "token";
    private const string KOffset = "offset";
    private const string KWidth = "width";
    private const string KBiosDefault = "biosdefault";
    private const string KOptions = "options";
    private const string KValue = "value";

    private static readonly Regex HeaderRegex = new(@"^-{2,}\s*(?<body>.*?)\s*-{2,}$", RegexOptions.Compiled);
    private static readonly Regex DashRuns = new(@"-{2,}", RegexOptions.Compiled);
    private static readonly Regex OptionRange = new(
        @"^(?<low>0x[0-9A-Fa-f]+|\d+)\s*(?:\.\.|\.\.|—|–|-)\s*(?<high>0x[0-9A-Fa-f]+|\d+)$",
        RegexOptions.Compiled);
    /// <summary>Bracketed option entry, e.g. "[00]Auto" or " *[01]Enabled".</summary>
    private static readonly Regex BracketOption = new(@"^\*?\[[0-9A-Fa-f]+\]", RegexOptions.Compiled);

    public static ScewinDocument Parse(string text, string? sourceName = null, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        var segments = new List<ScewinSegment>(lines.Count / 4 + 16);
        var items = new List<BiosSetting>();
        var builder = new ItemBuilder();
        var path = Array.Empty<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            if (progress is not null && (i & 1023) == 0)
                progress.Report(i * 100.0 / Math.Max(1, lines.Count));

            var raw = lines[i].Content + lines[i].Ending;
            var trimmed = lines[i].Content.Trim();

            // A new "Setup Question" ALWAYS starts a new record — flush any
            // open item first. This is the record separator for dumps without
            // blank lines or dashed rules.
            int eq = lines[i].Content.IndexOf('=');
            string key = eq >= 0 ? NormalizeKey(lines[i].Content.AsSpan(0, eq)) : "";
            string value = eq >= 0 ? StripComment(lines[i].Content[(eq + 1)..]) : "";

            if (key is KSetupQuestion)
            {
                builder.FlushInto(items, segments, path);
                builder.Begin();
                builder.Absorb(raw, key, value);
                continue;
            }

            if (builder.IsOpen)
            {
                // Option continuation lines ("         [01]Manual" or
                // "     *[02]Advanced") join the open item's Options field.
                if (key.Length == 0 &&
                    BracketOption.IsMatch(trimmed.TrimStart('*')))
                {
                    builder.AbsorbOptionContinuation(raw, trimmed);
                    continue;
                }
                builder.Absorb(raw, key, value);
                continue;
            }

            // Section headers and separators (only meaningful outside items).
            if (TryParseHeader(trimmed, out var headerPath))
            {
                path = headerPath;
                segments.Add(new HeaderSegment { Raw = raw, Path = headerPath });
                continue;
            }

            // Comments, banners, blanks, orphan lines — kept verbatim.
            segments.Add(new OtherSegment { Raw = raw });
        }
        builder.FlushInto(items, segments, path);
        progress?.Report(100);

        if (items.Count == 0)
            throw new ScewinParseException(
                "No 'Setup Question' entries were found — this doesn't look like a SCEWIN dump.");

        // Self-check: every "Setup Question" marker line must have produced
        // exactly one item. A mismatch means the record-splitting logic lost
        // records — fail loudly rather than silently showing a partial list.
        int markerCount = CountSetupQuestionMarkers(lines);
        if (markerCount != items.Count)
        {
            throw new ScewinParseException(
                $"Parser self-check failed: found {markerCount} 'Setup Question' records " +
                $"but produced {items.Count} settings. The record separator logic is broken — " +
                "please report this with the dump file.");
        }

        // Build the menu tree. Sections are keyed by their full path so repeated
        // headers with the same path merge into one node, in first-appearance order.
        // Flat dumps (the common case) put everything under the single root.
        var root = new BiosMenuSection("All settings", Array.Empty<string>(), parent: null);
        var byPath = new Dictionary<string, BiosMenuSection> { [PathKey(Array.Empty<string>())] = root };
        foreach (var item in items)
        {
            var section = EnsureSection(byPath, root, item.MenuPath);
            item.Section = section;
            section.AddSetting(item);
        }

        return new ScewinDocument
        {
            SourceName = sourceName ?? "scewin_dump.txt",
            RawText = text,
            Segments = segments,
            Items = items,
            RootSection = root,
        };
    }

    /// <summary>Splits text into (content-without-EOL, EOL) pairs, preserving original endings.</summary>
    internal static List<(string Content, string Ending)> SplitLines(string text)
    {
        var result = new List<(string, string)>(text.Length / 24 + 8);
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end;
            string ending;
            if (nl < 0)
            {
                end = text.Length;
                ending = string.Empty;
            }
            else if (nl > start && text[nl - 1] == '\r')
            {
                end = nl - 1;
                ending = "\r\n";
            }
            else
            {
                end = nl;
                ending = "\n";
            }
            result.Add((text[start..end], ending));
            if (nl < 0) break;
            start = nl + 1;
        }
        return result;
    }

    /// <summary>
    /// Strips a trailing "//" comment (and surrounding whitespace) from a
    /// field value. Quoted "//" inside quotes is preserved. Returns the
    /// comment-free value.
    /// </summary>
    internal static string StripComment(string value)
    {
        int idx = value.IndexOf("//", StringComparison.Ordinal);
        return (idx >= 0 ? value[..idx] : value).Trim();
    }

    private static int CountSetupQuestionMarkers(List<(string Content, string Ending)> lines)
    {
        int count = 0;
        foreach (var line in lines)
        {
            int eq = line.Content.IndexOf('=');
            if (eq < 0) continue;
            if (NormalizeKey(line.Content.AsSpan(0, eq)) is KSetupQuestion &&
                !line.Content.TrimStart().StartsWith("//"))
            {
                count++;
            }
        }
        return count;
    }

    private static bool TryParseHeader(string trimmed, out string[] path)
    {
        path = Array.Empty<string>();
        if (trimmed.Length < 5) return false;
        var match = HeaderRegex.Match(trimmed);
        if (!match.Success || match.Groups["body"].Value.Trim().Length == 0) return false;
        path = DashRuns.Split(trimmed)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        return path.Length > 0;
    }

    private static string NormalizeKey(ReadOnlySpan<char> keySpan)
    {
        var sb = keySpan.ToString().Trim().ToLowerInvariant();
        // Aliases seen across vendor dumps.
        if (sb is "question") return KSetupQuestion;
        if (sb is "default") return KBiosDefault;
        return sb.Replace(" ", "").Replace("_", "").Replace("biosdefault", KBiosDefault);
    }

    private static string PathKey(string[] path) => string.Join('\u0001', path);

    private static BiosMenuSection EnsureSection(
        Dictionary<string, BiosMenuSection> byPath, BiosMenuSection root, string[] path)
    {
        if (path.Length == 0) return root;
        var key = PathKey(path);
        if (byPath.TryGetValue(key, out var existing)) return existing;

        var parent = path.Length == 1 ? root : EnsureSection(byPath, root, path[..^1]);
        var section = new BiosMenuSection(path[^1], path, parent);
        parent.AttachChild(section);
        byPath[key] = section;
        return section;
    }

    private static string[] SplitMenuPath(string text) =>
        text.Split(new[] { '\\', '/', '|', '>' }, StringSplitOptions.TrimEntries |
                                                  StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();

    /// <summary>Accumulates one "Setup Question" item block and emits a <see cref="BiosSetting"/>.</summary>
    private sealed class ItemBuilder
    {
        private readonly List<string> _raw = new();
        private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);
        private readonly List<string> _optionContinuations = new();
        private int _valueLineIndex = -1;
        private string _currentMarkerToken = "";

        public bool IsOpen { get; private set; }

        public void Begin() => IsOpen = true;

        public void Absorb(string raw, string key, string value)
        {
            _raw.Add(raw);
            if (key.Length == 0) return;
            if (!_fields.ContainsKey(key)) _fields[key] = value;
            if (key == KValue && _valueLineIndex < 0) _valueLineIndex = _raw.Count - 1;
        }

        /// <summary>Joins a "[01]Manual" continuation line onto the Options field.</summary>
        public void AbsorbOptionContinuation(string raw, string trimmed)
        {
            _raw.Add(raw);
            _optionContinuations.Add(trimmed);
            // Track the starred continuation too — the star can appear on any line.
            if (trimmed.StartsWith('*'))
                _currentMarkerToken = ExtractRawToken(trimmed) ?? _currentMarkerToken;
        }

        public void FlushInto(List<BiosSetting> items, List<ScewinSegment> segments, string[] currentPath)
        {
            if (!IsOpen) return;
            IsOpen = false;

            if (_fields.TryGetValue(KSetupQuestion, out var question) && question.Length > 0)
            {
                var menuPath = _fields.TryGetValue("menu", out var menu) && menu.Length > 0
                    ? SplitMenuPath(menu)
                    : currentPath;
                var item = BuildItem(question, string.Concat(_raw), menuPath);
                items.Add(item);
                segments.Add(new ItemSegment { Item = item });
            }
            else if (_raw.Count > 0)
            {
                // Key/value text outside any item (shouldn't happen) — keep verbatim.
                segments.Add(new OtherSegment { Raw = string.Concat(_raw) });
            }

            _raw.Clear();
            _fields.Clear();
            _optionContinuations.Clear();
            _valueLineIndex = -1;
            _currentMarkerToken = "";
        }

        private BiosSetting BuildItem(string question, string rawBlock, string[] menuPath)
        {
            string? Get(string key) =>
                _fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

            var optionsRaw = Get(KOptions);
            string? value = Get(KValue);

            List<BiosOption>? options = null;
            BiosNumericRange? range = null;
            if (optionsRaw is not null)
            {
                ParseOptions(optionsRaw, _optionContinuations, out options, out range);
            }

            // Value resolution for script dumps with no "Value =" line:
            //   1. explicit "Value =&lt;550&gt;" line (numeric items)
            //   2. the "*" starred option (AMISCE marks the CURRENT value)
            //   3. BIOS Default as last resort
            if (string.IsNullOrEmpty(value))
            {
                if (_currentMarkerToken.Length > 0) value = _currentMarkerToken;
                else if (options is { Count: > 0 }) value = options[0].RawToken; // first option ≈ default/current
            }
            value ??= Get(KBiosDefault) ?? "";

            return new BiosSetting
            {
                SetupQuestion = question,
                HelpString = Get(KHelpString),
                Token = Get(KToken),
                Offset = Get(KOffset),
                Width = Get(KWidth),
                BiosDefault = Get(KBiosDefault),
                OptionsRaw = optionsRaw,
                Options = options,
                Range = range,
                OriginalValue = value,
                MenuPath = menuPath,
                RawBlock = rawBlock,
                ValueLineIndex = _valueLineIndex,
                Value = value,
            };
        }

        private static string? ExtractRawToken(string optionText)
        {
            var bare = optionText.TrimStart('*');
            int close = bare.IndexOf(']');
            return close > 1 ? bare[1..close] : null;
        }

        private static void ParseOptions(
            string optionsRaw, List<string> continuations,
            out List<BiosOption>? options, out BiosNumericRange? range)
        {
            options = null;
            range = null;

            // Continuation lines are real option entries, not comma parts.
            var parts = new List<string>();
            if (optionsRaw.Contains('[') && BracketOption.IsMatch(optionsRaw.TrimStart('*')))
            {
                // First entry lives on the Options line itself; continuations follow.
                parts.Add(optionsRaw.TrimStart('*').Trim());
            }
            else
            {
                parts.AddRange(optionsRaw.Split(',', StringSplitOptions.TrimEntries |
                                                        StringSplitOptions.RemoveEmptyEntries));
            }
            parts.AddRange(continuations.Select(c => c.TrimStart('*').Trim()));
            if (parts.Count == 0) return;

            // "0x50..0xB4" style: a single numeric range → free numeric editor with bounds.
            if (parts.Count == 1 && !optionsRaw.Contains('['))
            {
                var m = OptionRange.Match(parts[0]);
                if (m.Success &&
                    BiosSetting.TryParseNumber(m.Groups["low"].Value, out var low) &&
                    BiosSetting.TryParseNumber(m.Groups["high"].Value, out var high))
                {
                    range = low <= high
                        ? new BiosNumericRange(low, high, parts[0])
                        : new BiosNumericRange(high, low, parts[0]);
                    return;
                }
            }

            // Bracketed entries: "[00]Auto" → token "00", label "Auto".
            var list = new List<BiosOption>(parts.Count);
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                if (BracketOption.IsMatch(part.StartsWith('*') ? part[1..] : part))
                {
                    int close = part.IndexOf(']');
                    var token = part[1..close];
                    var label = part[(close + 1)..].Trim();
                    list.Add(new BiosOption(token, label.Length > 0 ? label : null));
                }
                else if (part.Contains('='))
                {
                    int eq = part.IndexOf('=');
                    var token = part[..eq].Trim();
                    var label = part[(eq + 1)..].Trim();
                    if (token.Length > 0) list.Add(new BiosOption(token, label.Length > 0 ? label : null));
                }
                else
                {
                    list.Add(new BiosOption(part, null));
                }
            }
            if (list.Count > 0) options = list;
        }
    }
}
