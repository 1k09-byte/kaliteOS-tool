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

    private static readonly object[] StyleKeys =
    {
        typeof(TextBlock),
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
    };

    private static string PrefPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "font-preference.txt");

    public static string Selected { get; private set; } = DefaultTag;

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
        catch {  }
    }

    public static void Save(string tag)
    {
        Selected = string.IsNullOrWhiteSpace(tag) ? DefaultTag : tag.Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefPath)!);
            File.WriteAllText(PrefPath, Selected);
        }
        catch {  }
    }

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

            resources["KaliteFontFamily"] = after;
            resources["KaliteDisplayFontFamily"] = after;
            resources["KaliteBodyFontFamily"] = DefaultFace;
            resources["XamlAutoFontFamily"] = DefaultFace;
            resources["ContentControlThemeFontFamily"] = DefaultFace;
            resources["TextControlThemeFontFamily"] = DefaultFace;

            Diag("Apply: rewriting styles");
            RewriteStyles(resources, before, after);
            Diag("Apply: styles rewritten");

            if (liveRoot is not null) Stamp(liveRoot, before, after);
        }
        catch (Exception ex)
        {
            Diag("Apply FAILED: " + ex);
        }
    }

    private static void RewriteStyles(ResourceDictionary root, FontFamily before, FontFamily after)
    {
        var entries = new List<(object Key, Style Style)>();
        foreach (var key in StyleKeys)
            if (root.TryGetValue(key, out var value) && value is Style style)
                entries.Add((key, style));
        if (entries.Count == 0) return;

        var cloneSet = new HashSet<Style>();
        foreach (var (_, style) in entries)
            if (HasFaceSetter(style, before))
                cloneSet.Add(style);

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
                catch {  }
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
