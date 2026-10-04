// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of this software, in any form, whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace kaliteConfig.Models;

/// <summary>
/// Outcome-based buckets for the default (Simple) view. These are deliberately NOT
/// the driver's own subsystems - "Performance and Latency" is a reason to care,
/// "3D Settings" is not.
/// </summary>
public enum NvidiaSettingCategory
{
    /// <summary>Anything curated that has no better home. Always the fallback.</summary>
    Other = 0,
    Antialiasing = 1,
    Sync = 2,
    Refresh = 3,
    TextureFiltering = 4,
    CommonSettings = 5,
    Rtx = 6,
    Hidden = 7,
}

public static class NvidiaSettingCategories
{
    public static string DisplayName(this NvidiaSettingCategory category) => category switch
    {
        NvidiaSettingCategory.Antialiasing => "Antialiasing",
        NvidiaSettingCategory.Sync => "Sync",
        NvidiaSettingCategory.Refresh => "Refresh",
        NvidiaSettingCategory.TextureFiltering => "Texture filtering",
        NvidiaSettingCategory.CommonSettings => "Common settings",
        NvidiaSettingCategory.Rtx => "RTX",
        NvidiaSettingCategory.Hidden => "Hidden",
        _ => "Other",
    };

    /// <summary>
    /// Category order in the UI.
    ///
    /// "Hidden" is last on purpose and is rendered muted. It holds the settings that
    /// exist only because NVIDIA's driver ships them for multi-GPU, stereoscopic,
    /// internal-tooling and legacy workarounds - real, but with no use on a normal
    /// single-GPU machine. They are kept because deleting them would move them into
    /// the raw Advanced list, which is strictly less usable, and because a machine
    /// that does need one (an SLI laptop, a 3D projector) would otherwise have no
    /// way to reach it.
    /// </summary>
    public static IReadOnlyList<NvidiaSettingCategory> Ordered { get; } = new[]
    {
        NvidiaSettingCategory.CommonSettings,
        NvidiaSettingCategory.Antialiasing,
        NvidiaSettingCategory.Sync,
        NvidiaSettingCategory.Refresh,
        NvidiaSettingCategory.TextureFiltering,
        NvidiaSettingCategory.Rtx,
        NvidiaSettingCategory.Hidden,
        NvidiaSettingCategory.Other,
    };

    /// <summary>True for the section that should be muted and collapsed by default.</summary>
    public static bool IsMuted(this NvidiaSettingCategory category) => category == NvidiaSettingCategory.Hidden;

    public static bool TryParseName(string? name, out NvidiaSettingCategory category)
    {
        category = NvidiaSettingCategory.Other;
        if (string.IsNullOrWhiteSpace(name)) return false;
        // Compare on a squashed key so "Common settings", "common-settings" and
        // "COMMON SETTINGS" all name the same bucket.
        string key = new string(name.Trim().ToLowerInvariant()
            .Replace("&", "and")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        switch (key)
        {
            case "antialiasing":
            case "aa":
                category = NvidiaSettingCategory.Antialiasing; return true;
            case "sync":
            case "vsync":
            case "vsynchronization":
            case "tearing":
                category = NvidiaSettingCategory.Sync; return true;
            case "refresh":
            case "refreshrate":
            case "framerate":
                category = NvidiaSettingCategory.Refresh; return true;
            case "texturefiltering":
            case "texture":
            case "filtering":
            case "anisotropic":
                category = NvidiaSettingCategory.TextureFiltering; return true;
            case "commonsettings":
            case "common":
            case "general":
            case "performance":
                category = NvidiaSettingCategory.CommonSettings; return true;
            case "rtx":
            case "dlss":
            case "upscaling":
            case "framegeneration":
            case "raytracing":
                category = NvidiaSettingCategory.Rtx; return true;
            case "hidden":
            case "advanced":
            case "other":
                category = NvidiaSettingCategory.Hidden; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Files a setting under the group the Profile Inspector gives it, so a setting the
    /// hand-maintained map has never heard of still lands somewhere sensible instead of
    /// dumping into "Other".
    ///
    /// This only routes; it never renames or reinterprets anything. The inspector's
    /// group names are its own and are matched loosely because they vary ("01 - 3D
    /// Settings" and "3D Settings" are the same place). A blank or missing group is
    /// "Other" rather than a guess, because putting a setting in the wrong bucket is
    /// worse than putting it in a plainly-unclassified one.
    /// </summary>
    public static NvidiaSettingCategory FromReferenceGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return NvidiaSettingCategory.Other;
        string key = new string(group.Trim().ToLowerInvariant()
            .Replace("&", "and")
            .Where(char.IsLetterOrDigit)
            .ToArray());

        // Anything multi-GPU, stereoscopic, OpenGL or cross-vendor is real but has no
        // use on a normal single-GPU machine, which is what "Hidden" is for.
        if (Contains(key, "opengl") || Contains(key, "stereo") || Contains(key, "sli")
            || Contains(key, "nvlink") || Contains(key, "compatib")) return NvidiaSettingCategory.Hidden;

        if (Contains(key, "antialias")) return NvidiaSettingCategory.Antialiasing;
        if (Contains(key, "texture") || Contains(key, "anisotropic")) return NvidiaSettingCategory.TextureFiltering;
        if (Contains(key, "upscaling") || Contains(key, "framegeneration")
            || Contains(key, "raytracing") || Contains(key, "dxr")) return NvidiaSettingCategory.Rtx;
        if (Contains(key, "sync") || Contains(key, "tearing")) return NvidiaSettingCategory.Sync;
        if (Contains(key, "refresh") || Contains(key, "framerate")) return NvidiaSettingCategory.Refresh;

        // "07 - System, Memory and Compute" deliberately falls through to the general
        // bucket. Hiding it would bury power, thermal and PCIe limits that every
        // machine has an opinion about.
        return NvidiaSettingCategory.CommonSettings;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
}

/// <summary>
/// Where a row's name came from. This is the honesty boundary: a name is only ever
/// the driver's own. <see cref="Curated"/> rows are the 3D page's hand-checked list
/// and <see cref="KnownAdvanced"/> rows are named by the driver SDK; a
/// <see cref="Unknown"/> row is a raw hex ID and nothing else, because a name we
/// made up would be worse than a number the user can look up.
/// </summary>
public enum NvidiaSettingProvenance
{
    /// <summary>On the curated NVCP-visible list, which the 3D settings page shows.</summary>
    Curated = 0,

    /// <summary>The driver SDK maps this ID to a known setting, so its name is the SDK's.</summary>
    KnownAdvanced = 1,

    /// <summary>No verified mapping anywhere. Raw hex ID only - never invent a name.</summary>
    Unknown = 2,
}

/// <summary>Which friendly control a default-view row gets. Never a raw value box.</summary>
public enum NvidiaSettingControlKind
{
    /// <summary>No usable driver-reported options: show the value as read-only text.</summary>
    ReadOnly = 0,
    Toggle = 1,
    Choice = 2,
}

/// <summary>
/// A row in the Simple view: the driver's own name, the driver's own option labels,
/// the current and default values as the driver reports them, the group it is filed
/// under, and whether it can be edited at all.
///
/// There is no description field. The driver publishes no prose for these settings -
/// its own description field is the setting's name repeated - so a sentence here
/// would be something this app made up, and a made-up sentence on a driver setting
/// is worse than no sentence at all. The hex ID is shown instead, because that is
/// something the user can verify and look up.
/// </summary>
public sealed record NvidiaSimpleSettingRow(
    uint Id,
    string Name,
    uint CurrentValue,
    uint DefaultValue,
    bool IsInherited,
    IReadOnlyList<Nvidia3DOption> Options,
    NvidiaSettingCategory Category,
    NvidiaSettingProvenance Provenance,
    bool IsEditable,
    string HexId,
    string? ValueLabel = null)
{
    /// <summary>
    /// False when this driver's own enumeration does not offer the setting, even
    /// though the Profile Inspector lists it. Such a row is shown so the page matches
    /// what the Inspector shows, but it is inert: there is nothing on this machine for
    /// it to act on, and <see cref="IsEditable"/> is false to match.
    /// </summary>
    public bool IsSupportedByDriver { get; init; } = true;

    /// <summary>
    /// Why a row is inert, in one sentence, shown under its name. Empty for every
    /// row the driver does offer, so the note is never decoration on a working row.
    /// </summary>
    public string? SupportNote { get; init; }

    public bool HasSupportNote => !string.IsNullOrWhiteSpace(SupportNote);

    /// <summary>Settings whose value is not an integer cannot be written by this editor.</summary>
    public bool IsInheriting => IsInherited;

    public NvidiaSettingControlKind ControlKind => NvidiaSimpleSettings.ResolveControlKind(Options, IsInherited);

    public bool HasOptions => Options.Any(o => o.Value != InheritSentinel);

    public const uint InheritSentinel = uint.MaxValue;

    /// <summary>Label for the current value, falling back to the raw number.</summary>
    public string CurrentLabel => ValueLabel is not null
        ? ValueLabel
        : NvidiaSimpleSettings.FormatValue(CurrentValue, Options, IsInherited);

    /// <summary>Label for the value this row had when it was read, before any staging.</summary>
    public string DefaultLabel => NvidiaSimpleSettings.FormatValue(DefaultValue, Options, IsInherited);
}

/// <summary>
/// Pure helpers behind the Simple view. No WinUI, no NvAPI: the driver only supplies
/// option values and labels, every decision about how to present them lives here so it
/// can be unit tested.
/// </summary>
public static class NvidiaSimpleSettings
{
    /// <summary>Label shown for a program-profile row that inherits from Global.</summary>
    public const string InheritLabel = "Use global setting";

    /// <summary>Label shown when a setting has no recognized options at all.</summary>
    public const string NoKnownOptionsLabel = "NVIDIA default";

    public static string Hex(uint id) => $"0x{id:X8}";

    /// <summary>
    /// Turns an SDK value identifier into readable words without changing its meaning:
    /// "PreferMaximum" becomes "Prefer maximum", "FXAAAllow" becomes "FXAA Allow".
    ///
    /// This is a purely mechanical split of an identifier that the driver SDK already
    /// published, so no meaning is invented. Anything that is not plain camel case -
    /// containing digits or symbols, such as "VCAA32X8V24" - is left exactly as the SDK
    /// spelled it, because mangling those would actually lose information.
    /// </summary>
    public static string PrettifyLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        string text = label.Trim();
        if (text.Length < 2) return text;
        if (!text.All(char.IsLetter)) return text;
        if (!char.IsUpper(text[0])) return text;

        // Split the identifier into words first, then decide each word's casing, so a
        // trailing acronym keeps its capitals ("OnIfFOS" -> "On if FOS").
        var words = new System.Collections.Generic.List<(string Text, bool WasAllUpper)>();
        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            char current = text[i];
            bool previousIsLower = char.IsLower(text[i - 1]);
            bool previousIsUpper = char.IsUpper(text[i - 1]);
            bool nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
            // New word at the last capital of a run ("FXAA|Allow") and at any capital
            // that follows a lowercase letter ("Prefer|Maximum").
            if (!char.IsUpper(current)) continue;
            if (previousIsLower || (previousIsUpper && nextIsLower))
            {
                words.Add((text[start..i], text[start..i].All(char.IsUpper)));
                start = i;
            }
        }
        words.Add((text[start..], text[start..].All(char.IsUpper)));

        var builder = new System.Text.StringBuilder(text.Length + 8);
        for (int w = 0; w < words.Count; w++)
        {
            string word = words[w].Text;
            if (w > 0) builder.Append(' ');
            // A run of three or more capitals is an acronym (FOS, FXAA, VRR); keep it.
            if (w == 0 || (words[w].WasAllUpper && word.Length >= 3)) builder.Append(word);
            else builder.Append(char.ToLowerInvariant(word[0])).Append(word[1..].ToLowerInvariant());
        }
        return builder.ToString();
    }

    /// <summary>
    /// Comparison key for option labels, so a preset written in friendly words still
    /// matches the SDK's identifier spelling on any driver.
    /// </summary>
    public static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        var chars = label.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    public static bool LabelsMatch(string? a, string? b)
    {
        string left = NormalizeLabel(a);
        return left.Length > 0 && left == NormalizeLabel(b);
    }

    /// <summary>
    /// Picks the friendliest control we can honestly render. A two-option On/Off
    /// setting becomes a toggle; anything richer becomes a dropdown; anything with
    /// no labeled options becomes read-only text rather than a misleading empty box.
    /// </summary>
    public static NvidiaSettingControlKind ResolveControlKind(
        IReadOnlyList<Nvidia3DOption> options, bool isInherited)
    {
        var real = options.Where(o => o.Value != NvidiaSimpleSettingRow.InheritSentinel).ToList();
        if (real.Count == 0) return NvidiaSettingControlKind.ReadOnly;
        // Exactly one of the two must read as the off state. If both or neither do,
        // a toggle would be a guess about which value disables the setting.
        if (real.Count != 2) return NvidiaSettingControlKind.Choice;
        if (IsMaskLabel(real[0].Label) || IsMaskLabel(real[1].Label)) return NvidiaSettingControlKind.Choice;
        return IsOffLabel(real[0].Label) != IsOffLabel(real[1].Label)
            ? NvidiaSettingControlKind.Toggle
            : NvidiaSettingControlKind.Choice;
    }

    /// <summary>
    /// True for the driver's "mask" placeholder - the value that means "no specific
    /// choice here" rather than a real setting. A switch must never treat one of
    /// these as the enabled state, so a setting carrying one stays a dropdown.
    /// </summary>
    public static bool IsMaskLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return label.Trim().EndsWith("mask", StringComparison.OrdinalIgnoreCase)
            || string.Equals(label.Trim(), "mask", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a driver-reported label reads as "this is the off state". Used only
    /// to decide whether a two-option setting can be a toggle; a label we cannot read
    /// as on or off makes the setting a dropdown instead of a guess.
    /// </summary>
    public static bool IsOffLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        string l = label.Trim().ToLowerInvariant();
        return l.Contains("off") || l.Contains("disable") || l.Contains("disallow") || l == "none";
    }

    /// <summary>Formats a value for display, preferring a real driver-reported label.</summary>
    public static string FormatValue(uint value, IReadOnlyList<Nvidia3DOption> options, bool isInherited)
    {
        if (isInherited) return InheritLabel;
        var match = options.FirstOrDefault(o => o.Value == value);
        if (match is not null) return match.Label;
        return NoKnownOptionsLabel;
    }

    /// <summary>
    /// The plain-English diff shown before every Apply, e.g.
    /// "Power management mode: Optimal power to Prefer maximum performance".
    /// </summary>
    public static string BuildDiffLine(string name, string from, string to)
        => $"{name}: {from} to {to}";

    /// <summary>Full plain-English diff over a set of staged changes.</summary>
    public static IReadOnlyList<string> BuildDiffLines(
        IEnumerable<(string Name, string From, string To)> changes)
        => changes.Select(c => BuildDiffLine(c.Name, c.From, c.To)).ToList();

    /// <summary>
    /// Filters search text over the driver's own setting name and its hex ID, so a
    /// user can paste in either the name they see in the NVIDIA Control Panel or the
    /// ID from a full profile editor and find the row either way.
    /// </summary>
    public static bool MatchesSearch(string? search, string name, string? hexId = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        string needle = search.Trim();
        return Contains(name, needle) || (hexId is not null && Contains(hexId, needle));
    }

    private static bool Contains(string? haystack, string needle)
        => (haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
}

/// <summary>Identity of one row in the Advanced enumeration.</summary>
public sealed record NvidiaAdvancedIdentity(NvidiaSettingProvenance Provenance, string Name);

/// <summary>
/// The honesty rule for naming a driver setting, kept separate from the driver call
/// so it can be unit tested without a GPU.
///
/// The rule has exactly one interesting branch. When the driver SDK - which already
/// ships inside a licensed dependency of this app - maps the ID to a known setting,
/// the name it publishes is used verbatim. When it does not, the row is the raw hex ID.
/// There is no third path: a row never gets a name this app made up, and a setting the
/// 3D page already lists never reaches here at all, so the two pages cannot show the
/// same setting twice.
/// </summary>
public static class NvidiaAdvancedClassifier
{
    public static NvidiaAdvancedIdentity Classify(uint id, bool sdkKnowsIt, string? sdkName)
    {
        string hex = NvidiaSimpleSettings.Hex(id);
        if (!sdkKnowsIt) return new NvidiaAdvancedIdentity(NvidiaSettingProvenance.Unknown, hex);
        return new NvidiaAdvancedIdentity(
            NvidiaSettingProvenance.KnownAdvanced,
            string.IsNullOrWhiteSpace(sdkName) ? hex : sdkName.Trim());
    }

    /// <summary>
    /// Splits raw enumerated IDs into "keep out of Advanced" (already curated) and
    /// "show in Advanced". De-duplicates, because the driver repeats some IDs across
    /// the mask sets of a profile.
    /// </summary>
    public static IReadOnlyList<uint> ExcludeCurated(IEnumerable<uint> rawIds, IEnumerable<uint> curatedIds)
    {
        var curated = new HashSet<uint>(curatedIds);
        var seen = new HashSet<uint>();
        var kept = new List<uint>();
        foreach (uint id in rawIds)
        {
            if (curated.Contains(id)) continue;
            if (!seen.Add(id)) continue;
            kept.Add(id);
        }
        return kept;
    }
}

/// <summary>
/// setting ID -> outcome category, loaded from an editable JSON file so the mapping
/// can be retuned without touching logic code.
/// </summary>
public sealed class NvidiaSettingCategoryMap
{
    /// <summary>
    /// Bumped whenever the shipped grouping changes shape, which is what tells the
    /// loader that a copy on disk was written for a different set of settings. Bump
    /// it when you add or move settings; users on an older number get the new file
    /// re-seeded instead of silently losing every new row into "Other".
    /// </summary>
    public const int CurrentSchemaVersion = 6;

    private readonly Dictionary<uint, NvidiaSettingCategory> _map;

    public NvidiaSettingCategoryMap(Dictionary<uint, NvidiaSettingCategory>? map = null)
        => _map = map ?? new Dictionary<uint, NvidiaSettingCategory>();

    public IReadOnlyDictionary<uint, NvidiaSettingCategory> Entries => _map;

    public int Count => _map.Count;

    /// <summary>Never throws and never guesses: an unlisted ID lands in "Other".</summary>
    public NvidiaSettingCategory CategoryFor(uint id)
        => _map.TryGetValue(id, out var category) ? category : NvidiaSettingCategory.Other;

    /// <summary>
    /// The user's map wins; anything it does not mention is filed under the group the
    /// Profile Inspector itself uses for that setting.
    ///
    /// The fallback exists because the map is hand-maintained and will always trail
    /// the driver. Routing by the inspector's own group means a setting added to a
    /// newer driver still lands somewhere sensible instead of dumping into "Other",
    /// without this app having to invent a bucket for it first.
    /// </summary>
    public NvidiaSettingCategory CategoryFor(uint id, string? referenceGroup)
        => _map.TryGetValue(id, out var category)
            ? category
            : NvidiaSettingCategories.FromReferenceGroup(referenceGroup);

    public static NvidiaSettingCategoryMap Empty { get; } = new();

    /// <summary>
    /// Reads just the schema version out of a config file, or 0 when it is missing or
    /// unreadable. Used to decide whether the on-disk copy still matches the settings
    /// this build knows about.
    /// </summary>
    public static int ReadSchemaVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var v)) return 0;
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>
    /// Parses the editable config. Tolerant by design: a hand-edited file with one bad
    /// entry keeps every other good entry instead of losing the whole mapping.
    /// Shape:
    ///   { "categories": { "Performance &amp; Latency": [ "0x1057EB71", ... ] } }
    /// IDs may be hex strings, decimal strings, or JSON numbers.
    /// </summary>
    public static NvidiaSettingCategoryMap Parse(string? json)
    {
        var map = new Dictionary<uint, NvidiaSettingCategory>();
        if (string.IsNullOrWhiteSpace(json)) return new NvidiaSettingCategoryMap(map);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return new NvidiaSettingCategoryMap(map); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new NvidiaSettingCategoryMap(map);
            if (!doc.RootElement.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Object)
                return new NvidiaSettingCategoryMap(map);

            foreach (var categoryProp in categories.EnumerateObject())
            {
                if (!NvidiaSettingCategories.TryParseName(categoryProp.Name, out var category)) continue;
                if (categoryProp.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in categoryProp.Value.EnumerateArray())
                {
                    if (TryParseId(item, out uint id)) map[id] = category;
                }
            }
        }
        return new NvidiaSettingCategoryMap(map);
    }

    private static bool TryParseId(JsonElement element, out uint id)
    {
        id = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetUInt32(out id);
            case JsonValueKind.String:
                return TryParseIdText(element.GetString(), out id);
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads a raw setting ID or value typed by the user. Accepts "0x1F", "0X1F",
    /// "#1F" and plain decimal, and refuses anything else, so a mistyped value never
    /// reaches the driver.
    /// </summary>
    public static bool TryParseIdText(string? text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        bool hex = false;
        // The marker is one or two characters wide ("#" vs "0x"), so it has to be
        // stripped by its own length rather than by a fixed two.
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { hex = true; t = t[2..]; }
        else if (t.StartsWith("#", StringComparison.Ordinal)) { hex = true; t = t[1..]; }
        if (t.Length == 0) return false;
        return uint.TryParse(t, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out id);
    }
}
