// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use, but the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace kaliteConfig.Models;

/// <summary>
/// One setting as the bundled NVIDIA Profile Inspector describes it.
/// </summary>
public sealed record NvidiaDriverReferenceEntry(
    uint Id,
    string Name,
    string Description,
    string Group,
    uint? Default,
    IReadOnlyList<Nvidia3DOption> Options,
    bool IsHidden = false)
{
    /// <summary>
    /// True when the description is the inspector's own generated placeholder rather
    /// than something NVIDIA wrote. The file contains 312 of them, in two spellings:
    ///
    ///   Controls the NVIDIA driver behavior associated with "X". This setting affects ...
    ///   Controls the predefined driver behavior associated with "X". This setting is used for ...
    ///
    /// Both restate the setting's own name back at you and then name a whole
    /// subsystem - "affects presentation timing, frame pacing, refresh policy" says
    /// the same thing for every setting in the file. Under a setting's name that is
    /// noise dressed up as documentation, so the caller prefers a blank line to a
    /// sentence that helps nobody. 382 of the 812 entries have real prose; those
    /// are kept.
    /// </summary>
    public bool DescriptionIsBoilerplate
    {
        get
        {
            string d = Description.Trim();
            if (d.Length == 0) return true;
            return d.StartsWith("Controls the NVIDIA driver behavior associated with", StringComparison.Ordinal)
                || d.StartsWith("Controls the predefined driver behavior associated with", StringComparison.Ordinal);
        }
    }

    /// <summary>The description when it is worth showing, otherwise empty.</summary>
    public string UsefulDescription => DescriptionIsBoilerplate ? string.Empty : Description.Trim();

    /// <summary>
    /// True when the file actually names this setting, rather than leaving the loader
    /// to synthesise "0x1F2E3D4C (Unknown)" for an ID it documents nothing about.
    ///
    /// This is the bar for showing a setting at all. A row whose only content is an
    /// identifier adds nothing the user did not already have, and there are 80 of
    /// them in this file.
    /// </summary>
    public bool HasRealName => !string.IsNullOrWhiteSpace(Name)
        && !Name.EndsWith("(Unknown)", StringComparison.OrdinalIgnoreCase)
        && !Name.EndsWith("(Driver)", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the reference data that ships beside the Profile Inspector, so the
/// settings page can show a setting's real name, what it is for, and what its
/// values are called instead of a bare number.
///
/// The driver publishes no prose of its own - its description field is the name
/// repeated - so this file is where real names, descriptions and value labels come
/// from. It documents 812 settings in total, of which 66 of the 130 this driver
/// reports are covered; 54 of those 66 carry prose someone actually wrote. The rest
/// keep the driver's own name and get no description, which is the honest outcome.
/// Nothing here is written by this app: every string is the inspector's, and where
/// the inspector is silent the row stays silent too.
/// </summary>
public static class NvidiaDriverReference
{
    private static readonly object Gate = new();
    private static Dictionary<uint, NvidiaDriverReferenceEntry>? _byId;
    private static bool _attempted;

    /// <summary>Where the data is looked for, exposed for tests and diagnostics.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "nvidiaProfileInspector", "Reference.xml");

    /// <summary>How many settings were loaded. Zero when the file is missing.</summary>
    public static int Count
    {
        get { EnsureLoaded(); return _byId?.Count ?? 0; }
    }

    /// <summary>
    /// Every setting the Profile Inspector lists by default: one it names, and one it
    /// has not marked hidden.
    ///
    /// This is the list the settings page compares the driver against, and the reason
    /// the page can now show what the Inspector shows. The driver advertises 130
    /// settings here; this file names 523 more that it does not, and those are real
    /// settings that simply do not apply to this machine. A driver that does support
    /// one moves it out of this list and into the live rows with no code change.
    /// </summary>
    public static IReadOnlyList<NvidiaDriverReferenceEntry> VisibleEntries
    {
        get { EnsureLoaded(); return _visible ??= VisibleFrom(_byId); }
    }

    private static IReadOnlyList<NvidiaDriverReferenceEntry>? _visible;

    /// <summary>
    /// The same filter, over any parsed set. Separated out so the rule can be tested
    /// against the shipped file directly: the file is not copied to the test output,
    /// so the cached property is empty there by design.
    /// </summary>
    public static IReadOnlyList<NvidiaDriverReferenceEntry> VisibleFrom(
        IReadOnlyDictionary<uint, NvidiaDriverReferenceEntry>? entries)
    {
        if (entries is null) return Array.Empty<NvidiaDriverReferenceEntry>();
        return entries.Values
            .Where(e => e.HasRealName && !e.IsHidden)
            .OrderBy(e => e.Group, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryGet(uint id, out NvidiaDriverReferenceEntry entry)
    {
        EnsureLoaded();
        if (_byId is not null && _byId.TryGetValue(id, out var found))
        {
            entry = found;
            return true;
        }
        entry = default!;
        return false;
    }

    /// <summary>Force a re-read. Tests use this; the app never needs it.</summary>
    public static void Reload()
    {
        lock (Gate) { _byId = null; _visible = null; _attempted = false; }
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (_attempted) return;
        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                _byId = Load(FilePath);
            }
            catch
            {
                // A missing or malformed reference file must never stop the page:
                // every row simply falls back to the driver's own reporting.
                _byId = new Dictionary<uint, NvidiaDriverReferenceEntry>();
            }
        }
    }

    /// <summary>
    /// Parses the reference XML. Public so the parsing can be tested against a
    /// string without touching the filesystem.
    ///
    /// Never throws. A truncated or corrupt file must not be able to take the
    /// settings page down with it; it just means every row falls back to the
    /// driver's own reporting, which is a working page, not a broken one.
    /// </summary>
    public static Dictionary<uint, NvidiaDriverReferenceEntry> Parse(string xml)
    {
        var result = new Dictionary<uint, NvidiaDriverReferenceEntry>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var setting in document.Descendants("CustomSetting"))
        {
            string? hexId = (string?)setting.Element("HexSettingID");
            if (!TryParseHexId(hexId, out uint id)) continue;

            var options = new List<Nvidia3DOption>();
            foreach (var value in setting.Elements("SettingValues").Elements("CustomSettingValue"))
            {
                string? label = (string?)value.Element("UserfriendlyName");
                if (string.IsNullOrWhiteSpace(label)) continue;
                // A value the file spells as hex is what gets written; a value it
                // omits is not an option this app can offer.
                if (TryParseHexId((string?)value.Element("HexValue"), out uint v))
                    options.Add(new Nvidia3DOption(v, label.Trim()));
            }

            result[id] = new NvidiaDriverReferenceEntry(
                id,
                ((string?)setting.Element("UserfriendlyName") ?? string.Empty).Trim(),
                ((string?)setting.Element("Description") ?? string.Empty).Trim(),
                ((string?)setting.Element("GroupName") ?? string.Empty).Trim(),
                TryParseHexId((string?)setting.Element("OverrideDefault"), out uint def) ? def : null,
                options,
                IsHidden: string.Equals(
                    ((string?)setting.Element("Hidden"))?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        }
        return result;
    }

    private static Dictionary<uint, NvidiaDriverReferenceEntry> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<uint, NvidiaDriverReferenceEntry>();
        return Parse(File.ReadAllText(path));
    }

    private static bool TryParseHexId(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
