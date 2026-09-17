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
/// Parses SCEWIN (SCEWIN_64.exe /o /s) dump text into a <see cref="ScewinDocument"/>.
///
/// Design goals:
///  - Tolerant: splits key/value pairs on the FIRST '=' per line and trims, so
///    "= alignment", extra whitespace, CRLF vs LF and missing optional fields
///    are all accepted. Unknown keys are kept verbatim, not dropped.
///  - Round-trip faithful: every line of the file lands in exactly one segment
///    whose text is preserved verbatim, so the exporter can rebuild the file
///    byte-for-byte and only rewrite "Value" lines of edited items.
///  - Pure: text in, model out — no file I/O, no UI dependencies. Throws
///    <see cref="ScewinParseException"/> only when the text contains no setup
///    items at all (i.e. it is not a SCEWIN dump).
///
/// Assumed grammar (see Docs/BiosManager.md for details and how to adjust):
///   - Item block starts at a "Setup Question = ..." line.
///   - A block ends at a separator line (dashes/equals only), a section header
///     line, or the next "Setup Question" line.
///   - Section headers look like "----- Advanced ----- CPU Configuration -----"
///     and carry the absolute menu path; each dash-run-separated token is a level.
///   - Optional per-item override: "Menu = Advanced\CPU Configuration".
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
    private const string KMenu = "menu";

    private static readonly Regex HeaderRegex = new(@"^-{2,}\s*(?<body>.*?)\s*-{2,}$", RegexOptions.Compiled);
    private static readonly Regex DashRuns = new(@"-{2,}", RegexOptions.Compiled);
    private static readonly Regex OptionRange = new(
        @"^(?<low>0x[0-9A-Fa-f]+|\d+)\s*(?:\.\.|\.\.|—|–|-)\s*(?<high>0x[0-9A-Fa-f]+|\d+)$",
        RegexOptions.Compiled);

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

            // Section headers and separators close any open item block first.
            if (TryParseHeader(trimmed, out var headerPath))
            {
                builder.FlushInto(items, segments, path);
                path = headerPath;
                segments.Add(new HeaderSegment { Raw = raw, Path = headerPath });
                continue;
            }
            if (IsSeparator(trimmed))
            {
                builder.FlushInto(items, segments, path);
                segments.Add(new OtherSegment { Raw = raw });
                continue;
            }

            int eq = lines[i].Content.IndexOf('=');
            string key = eq >= 0 ? NormalizeKey(lines[i].Content.AsSpan(0, eq)) : "";
            string value = eq >= 0 ? lines[i].Content[(eq + 1)..].Trim() : "";

            if (builder.IsOpen)
            {
                builder.Absorb(raw, key, value);
                continue;
            }

            if (key is KSetupQuestion)
            {
                builder.Begin();
                builder.Absorb(raw, key, value);
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

        // Build the menu tree. Sections are keyed by their full path so repeated
        // headers with the same path merge into one node, in first-appearance order.
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

    private static bool IsSeparator(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        foreach (var c in trimmed)
            if (c is not ('-' or '=' or '~')) return false;
        return true;
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
        private int _valueLineIndex = -1;

        public bool IsOpen { get; private set; }

        public void Begin() => IsOpen = true;

        public void Absorb(string raw, string key, string value)
        {
            _raw.Add(raw);
            if (key.Length == 0) return;
            if (!_fields.ContainsKey(key)) _fields[key] = value;
            if (key == KValue && _valueLineIndex < 0) _valueLineIndex = _raw.Count - 1;
        }

        public void FlushInto(List<BiosSetting> items, List<ScewinSegment> segments, string[] currentPath)
        {
            if (!IsOpen) return;
            IsOpen = false;

            if (_fields.TryGetValue(KSetupQuestion, out var question) && question.Length > 0)
            {
                var menuPath = _fields.TryGetValue(KMenu, out var menu) && menu.Length > 0
                    ? SplitMenuPath(menu)
                    : currentPath;
                items.Add(BuildItem(question, string.Concat(_raw), menuPath));
            }
            else if (_raw.Count > 0)
            {
                // Key/value text outside any item (shouldn't happen) — keep verbatim.
                segments.Add(new OtherSegment { Raw = string.Concat(_raw) });
            }

            _raw.Clear();
            _fields.Clear();
            _valueLineIndex = -1;
        }

        private BiosSetting BuildItem(string question, string rawBlock, string[] menuPath)
        {
            string? Get(string key) =>
                _fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

            var optionsRaw = Get(KOptions);
            List<BiosOption>? options = null;
            BiosNumericRange? range = null;
            if (optionsRaw is not null)
            {
                ParseOptions(optionsRaw, out options, out range);
            }

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
                OriginalValue = _fields.TryGetValue(KValue, out var value) ? value : "",
                MenuPath = menuPath,
                RawBlock = rawBlock,
                ValueLineIndex = _valueLineIndex,
                Value = _fields.TryGetValue(KValue, out var v2) ? v2 : "",
            };
        }

        private static void ParseOptions(
            string optionsRaw, out List<BiosOption>? options, out BiosNumericRange? range)
        {
            options = null;
            range = null;

            var parts = optionsRaw.Split(',', StringSplitOptions.TrimEntries |
                                                   StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            // "0x50..0xB4" style: a single numeric range → free numeric editor with bounds.
            if (parts.Length == 1)
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

            // Otherwise: enumerated list of "token=label" pairs or bare tokens.
            var list = new List<BiosOption>(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
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
