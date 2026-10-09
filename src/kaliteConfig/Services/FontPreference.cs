// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Services;

/// <summary>
/// The user's app-font choice (Settings > App font) and the machinery that
/// puts it in front of every piece of text in the app.
///
/// The face is baked into XAML at parse time in three places: the font
/// resource keys (KaliteFontFamily, XamlAutoFontFamily,
/// ContentControlThemeFontFamily), the explicit FontFamily setters inside the
/// text styles (implicit TextBlock style, the redefined WinUI caption/body/
/// title styles, the Kalite* styles, KaliteButton and its dependents), and
/// FontFamily attributes in already-parsed windows. So choosing a font means:
///
///   1. startup: rewrite the resource keys and CLONE every style that carries
///      the old face (plus every style that BasedOn-chains into one) BEFORE
///      MainWindow parses - from then on every page, dialog and popup resolves
///      the new face fresh;
///   2. live change: the same rewrite, then stamp a local FontFamily onto the
///      already-loaded visual tree - a local value outranks a style setter, so
///      existing elements flip immediately. Elements are only stamped when
///      their current face actually IS the old app face, so monospace readouts
///      (Consolas, Cascadia) and symbol-glyph fonts keep theirs.
///
/// INVARIANT: every runtime write goes into the APPLICATION ROOT dictionary
/// (Application.Resources), never into a merged dictionary. Merged XAML
/// dictionaries have Source set, and a local value in one poisons XAML
/// loading - the next Application.LoadComponent (a page parse) dies with
/// XamlParseException "Local values are not allowed in resource dictionary
/// with Source set" (0x8000FFFF). Root entries shadow merged ones for every
/// later lookup, so shadowing at the root is both safe and sufficient.
///
/// "Default" is the default WinUI face (Segoe UI). The three bundled fonts
/// (Jim Nightshade, Uncial Antiqua, Cormorant Garamond) are opt-in; the
/// preference is applied at startup from a saved value, and the shipped XAML
/// face is converted on first Apply.
/// </summary>
internal static class FontPreference
{
    public const string DefaultTag = "Default";
    public const string JimTag = "JimNightshade";
    public const string UncialTag = "UncialAntiqua";
    public const string CormorantTag = "CormorantGaramond";

    private static readonly FontFamily DefaultFace = new("Segoe UI");
    private static readonly FontFamily Uncial =
        new("ms-appx:///Assets/Fonts/UncialAntiqua-Regular.ttf#Uncial Antiqua");
    private static readonly FontFamily Jim =
        new("ms-appx:///Assets/Fonts/JimNightshade-Regular.ttf#Jim Nightshade");
    private static readonly FontFamily Cormorant =
        new("ms-appx:///Assets/Fonts/CormorantGaramond-Regular.ttf#Cormorant Garamond");

    /// <summary>
    /// Every resource key whose style carries the app face, directly or through
    /// a BasedOn chain. A fixed list on purpose: enumerating a merged
    /// (Source-set) dictionary's Keys is not reliable at startup, and these are
    /// the only styles in the app that bake in the face (KaliteTheme.xaml and
    /// the implicit TextBlock style in App.xaml).
    /// </summary>
    private static readonly object[] StyleKeys =
    {
        typeof(TextBlock),          // implicit TextBlock style (App.xaml)
        "CaptionTextBlockStyle",
        "BodyTextBlockStyle",
        "BodyStrongTextBlockStyle",
        "TitleTextBlockStyle",
        "SubtitleTextBlockStyle",
        "HeaderTextBlockStyle",
        "DefaultContentDialogStyle",
        "KaliteDisplayText",
        "KalitePageTitle",
        "KaliteSectionTitle",
        "KaliteEyebrow",
        "KaliteButton",
        typeof(Button),             // implicit Button style (KaliteTheme.xaml)
        "KaliteAccentButton",
        "AccentButtonStyle",
        "KaliteSubtleButton",
        "KaliteIconButton",
    };

    private static string PrefPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "font-preference.txt");

    /// <summary>Chosen tag; <see cref="DefaultTag"/> until the user picks one.</summary>
    public static string Selected { get; private set; } = DefaultTag;

    /// <summary>Reads the saved choice. Called once from App before any window XAML.</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(PrefPath))
            {
                var tag = File.ReadAllText(PrefPath).Trim();
                if (tag.Length > 0) Selected = tag;
            }
        }
        catch { /* an unreadable preference must never break startup */ }
    }

    /// <summary>Persists the choice (best effort) and updates <see cref="Selected"/>.</summary>
    public static void Save(string tag)
    {
        Selected = string.IsNullOrWhiteSpace(tag) ? DefaultTag : tag.Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefPath)!);
            File.WriteAllText(PrefPath, Selected);
        }
        catch { /* the choice still applies for this session */ }
    }

    /// <summary>
    /// The face a tag stands for. Default = the default WinUI font (Segoe UI).
    /// </summary>
    public static FontFamily Resolve(string tag) => tag switch
    {
        JimTag => Jim,
        UncialTag => Uncial,
        CormorantTag => Cormorant,
        _ => DefaultFace,
    };

    private static bool Same(FontFamily? a, FontFamily? b) =>
        a is not null && b is not null &&
        string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies <see cref="Selected"/> to the running app: rewrites the three
    /// font resource keys, re-creates every style that baked in the old face
    /// (all shadowed into the application root dictionary), and - when
    /// <paramref name="liveRoot"/> is given (a font change made while the UI is
    /// up) - restamps the loaded visual tree. No-op when the resolved face is
    /// already the applied one.
    /// </summary>
    public static void Apply(DependencyObject? liveRoot = null)
    {
        try
        {
            var resources = Application.Current.Resources;
            Diag($"Apply: Selected={Selected}");
            if (!resources.TryGetValue("KaliteFontFamily", out var current) ||
                current is not FontFamily before)
            {
                Diag($"Apply: early-return, current={current?.GetType().Name ?? "null"}");
                return;
            }

            var after = Resolve(Selected);
            Diag($"Apply: before='{before.Source}' after='{after.Source}' same={Same(before, after)}");
            if (Same(before, after)) return;

            // 1. The font keys, shadowed at the root so every later
            //    {StaticResource}/{ThemeResource} lookup resolves the new face.
            resources["KaliteFontFamily"] = after;
            resources["XamlAutoFontFamily"] = after;
            resources["ContentControlThemeFontFamily"] = after;

            // 2. The styles that baked the old face in while the theme loaded.
            Diag("Apply: rewriting styles");
            RewriteStyles(resources, before, after);
            Diag("Apply: styles rewritten");

            // 3. Anything already on screen.
            if (liveRoot is not null) Stamp(liveRoot, before, after);
        }
        catch (Exception ex)
        {
            Diag("Apply FAILED: " + ex);
            // A font change must never take the app down; worst case the new
            // face lands after the next launch (the preference is persisted).
        }
    }

    /// <summary>
    /// Re-creates every listed style whose own FontFamily setter held the old
    /// face, plus every collected style that chains (BasedOn) into one of
    /// those, and writes the clones back UNDER THE SAME KEYS into the
    /// application root dictionary. The originals in the merged theme
    /// dictionary are left untouched - writing there poisons XAML loading (see
    /// the class doc). Clones are BasedOn-remapped so no chain leads back to a
    /// stale face.
    /// </summary>
    private static void RewriteStyles(ResourceDictionary root, FontFamily before, FontFamily after)
    {
        var entries = new List<(object Key, Style Style)>();
        foreach (var key in StyleKeys)
            if (root.TryGetValue(key, out var value) && value is Style style)
                entries.Add((key, style));
        if (entries.Count == 0) return;

        // Styles that carry the old face themselves...
        var cloneSet = new HashSet<Style>();
        foreach (var (_, style) in entries)
            if (HasFaceSetter(style, before))
                cloneSet.Add(style);

        // ...plus everything that chains into one, transitively. A dependent
        // without its own FontFamily setter (AccentButtonStyle, the implicit
        // Button style) would otherwise keep pointing at the stale original.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (_, style) in entries)
            {
                if (!cloneSet.Contains(style) &&
                    style.BasedOn is not null &&
                    cloneSet.Contains(style.BasedOn))
                {
                    cloneSet.Add(style);
                    grew = true;
                }
            }
        }

        if (cloneSet.Count == 0) return;

        var clones = new Dictionary<Style, Style>();
        foreach (var style in cloneSet)
            clones[style] = CloneStyle(style, before, after);

        foreach (var style in cloneSet)
            if (style.BasedOn is not null && clones.TryGetValue(style.BasedOn, out var basedOn))
                clones[style].BasedOn = basedOn;

        foreach (var (key, style) in entries)
            if (clones.TryGetValue(style, out var clone))
                try { root[key] = clone; }
                catch { /* a refused shadow keeps its entry; the stamp still fixes live text */ }
    }

    private static bool HasFaceSetter(Style style, FontFamily face)
    {
        foreach (var setterBase in style.Setters)
        {
            if (setterBase is Setter setter && setter.Value is FontFamily font && Same(font, face))
                return true;
        }
        return false;
    }

    private static Style CloneStyle(Style source, FontFamily before, FontFamily after)
    {
        var clone = new Style(source.TargetType);
        foreach (var setterBase in source.Setters)
        {
            if (setterBase is not Setter setter) continue;
            object value = setter.Value;
            if (value is FontFamily font && Same(font, before))
                value = after;
            clone.Setters.Add(new Setter(setter.Property, value));
        }
        if (source.BasedOn is not null) clone.BasedOn = source.BasedOn;
        return clone;
    }

    /// <summary>
    /// Walks the loaded visual tree top-down and swaps the old face for the new
    /// one where it appears. Top-down matters: once a parent Control is stamped,
    /// its children read the new face through inheritance and are skipped, so
    /// only elements that carry the old face themselves (a style setter or a
    /// local value) are touched. Anything else - Consolas readouts, symbol
    /// glyph fonts - does not match and is left alone.
    /// </summary>
    public static void Stamp(DependencyObject node, FontFamily before, FontFamily after)
    {
        switch (node)
        {
            case TextBlock textBlock:
                if (Same(textBlock.FontFamily, before)) textBlock.FontFamily = after;
                StampInlines(textBlock.Inlines, before, after);
                break;
            case Control control:
                if (Same(control.FontFamily, before)) control.FontFamily = after;
                break;
            case ContentPresenter presenter:
                if (Same(presenter.FontFamily, before)) presenter.FontFamily = after;
                break;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            Stamp(VisualTreeHelper.GetChild(node, i), before, after);
    }

    private static void StampInlines(InlineCollection inlines, FontFamily before, FontFamily after)
    {
        foreach (var inline in inlines)
        {
            if (inline is TextElement element && Same(element.FontFamily, before))
                element.FontFamily = after;
            if (inline is Span span)
                StampInlines(span.Inlines, before, after);
        }
    }

    private static void Diag(string line)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "font-debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
        }
        catch { }
    }
}
