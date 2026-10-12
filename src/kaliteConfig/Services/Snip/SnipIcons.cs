// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>
/// Single source of truth for every icon used by the Snip feature.
/// All codepoints verified against Microsoft's Segoe Fluent Icons glyph table
/// (learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font).
/// Font family resolves via SymbolThemeFontFamily so overlay windows resolve
/// the same font as the main window. Prefer these over SymbolIcon so the
/// Snip feature uses exactly one icon mechanism.
/// </summary>
public static class SnipIcons
{
    public const string FontFamilyResource = "{ThemeResource SymbolThemeFontFamily}";

    // Capture modes (dual-font verified; F407/F408 RectangularClipping/FreeFormClipping
    // are prettier but Fluent-only, so tiles use these to render on Win10 too)
    public const string Region = "\uE7A8";      // Crop
    public const string Window = "\uE8A7";      // OpenInNewWindow
    public const string Fullscreen = "\uE740";  // FullScreen
    public const string Freeform = "\uE7C9";    // TouchPointer
    public const string Delayed = "\uE916";     // Stopwatch
    public const string RepeatLast = "\uE72C";  // Refresh
    public const string Clipboard = "\uE77F";   // Paste

    // Overlay tools (dual-font verified: present in Segoe MDL2 Assets AND Fluent,
    // so they render on Win10 and Win11; meanings checked against both glyph tables)
    public const string Select = "\uE8B0";      // Click (pointer cursor)
    public const string Arrow = "\uE72A";       // Forward
    public const string Line = "\uE738";        // Remove (horizontal stroke)
    public const string Rectangle = "\uE73A";   // CheckboxComposite (nested squares)
    public const string Ellipse = "\uEA3A";     // CircleRing
    public const string Pen = "\uED63";         // Pencil
    public const string Highlighter = "\uED64"; // Marker
    public const string Text = "\uE8D2";        // Font
    public const string Number = "\uEC1B";      // Badge
    public const string Blur = "\uE8F8";        // BlockContact (redaction)
    public const string Spotlight = "\uE890";   // View (eye: look here)
    public const string Sticker = "\uE899";     // Emoji

    // History / output
    public const string Undo = "\uE7A7";        // Undo
    public const string Redo = "\uE7A6";        // Redo
    public const string Save = "\uE74E";        // Save
    public const string Copy = "\uE8C8";        // Copy
    public const string Pin = "\uE718";         // Pin
    public const string Ocr = "\uE8FE";         // Scan
    public const string Close = "\uE711";       // Cancel

    // Formatting row
    public const string Color = "\uE790";       // Color
    public const string Thickness = "\uEDA8";   // BrushSize
    public const string Fill = "\uEA3B";        // CircleFill
    public const string FontSize = "\uE8E9";    // FontSize
    public const string Eyedropper = "\uEF3C";  // Eyedropper

    // Quick tools
    public const string OpenFile = "\uE8E5";    // OpenFile
    public const string CopyText = "\uE8FE";    // Scan
    public const string ColorPicker = "\uEF3C"; // Eyedropper
    public const string Ruler = "\uED5E";       // Ruler
    public const string QrScan = "\uED14";      // QRCode

    // Misc
    public const string Edit = "\uE70F";        // Edit
    public const string Open = "\uE8E5";        // OpenFile
    public const string CopyPath = "\uE8C8";    // Copy
    public const string Rename = "\uE8AC";      // Rename
    public const string Favorite = "\uE734";    // FavoriteStar
    public const string FavoriteFill = "\uE735";// FavoriteStarFill
    public const string Folder = "\uE8B7";      // Folder
    public const string Delete = "\uE74D";      // Delete
    public const string Check = "\uE73E";       // CheckMark
    public const string Import = "\uE8B5";      // Import
    public const string Settings = "\uE713";    // Settings
    public const string Info = "\uE946";        // Info

    /// <summary>Returns the glyph for a name, for inline diagnostics like the Quick tools hints.</summary>
    public static string Pick(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).Glyph ?? "";

    /// <summary>(Name, glyph) for every icon - used by the debug Icon test page.</summary>
    public static IReadOnlyList<(string Name, string Glyph)> All { get; } = new List<(string, string)>
    {
        ("Region", Region), ("Window", Window), ("Fullscreen", Fullscreen),
        ("Freeform", Freeform), ("Delayed", Delayed), ("RepeatLast", RepeatLast),
        ("Clipboard", Clipboard), ("Select", Select), ("Arrow", Arrow),
        ("Line", Line), ("Rectangle", Rectangle), ("Ellipse", Ellipse),
        ("Pen", Pen), ("Highlighter", Highlighter), ("Text", Text),
        ("Number", Number), ("Blur", Blur), ("Spotlight", Spotlight),
        ("Sticker", Sticker),
        ("Undo", Undo), ("Redo", Redo), ("Save", Save), ("Copy", Copy),
        ("Pin", Pin), ("Ocr", Ocr), ("Close", Close), ("Color", Color),
        ("Thickness", Thickness), ("Fill", Fill), ("FontSize", FontSize),
        ("Eyedropper", Eyedropper), ("OpenFile", OpenFile), ("CopyText", CopyText),
        ("ColorPicker", ColorPicker), ("Ruler", Ruler), ("QrScan", QrScan),
        ("Edit", Edit), ("Open", Open), ("CopyPath", CopyPath),
        ("Rename", Rename), ("Favorite", Favorite), ("FavoriteFill", FavoriteFill),
        ("Folder", Folder), ("Delete", Delete), ("Check", Check),
        ("Import", Import), ("Settings", Settings), ("Info", Info),
    };
}
