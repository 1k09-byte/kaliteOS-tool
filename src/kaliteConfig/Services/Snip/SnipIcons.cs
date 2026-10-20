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

public static class SnipIcons
{
    public const string FontFamilyResource = "{ThemeResource SymbolThemeFontFamily}";

    public const string Region = "\uE7A8";
    public const string Window = "\uE8A7";
    public const string Fullscreen = "\uE740";
    public const string Freeform = "\uE7C9";
    public const string Delayed = "\uE916";
    public const string RepeatLast = "\uE72C";
    public const string Clipboard = "\uE77F";

    public const string Select = "\uE8B0";
    public const string Arrow = "\uE72A";
    public const string Line = "\uE738";
    public const string Rectangle = "\uE73A";
    public const string Ellipse = "\uEA3A";
    public const string Pen = "\uED63";
    public const string Highlighter = "\uED64";
    public const string Text = "\uE8D2";
    public const string Number = "\uEC1B";
    public const string Blur = "\uE8F8";
    public const string Spotlight = "\uE890";
    public const string Sticker = "\uE899";

    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string Save = "\uE74E";
    public const string Copy = "\uE8C8";
    public const string Pin = "\uE718";
    public const string Ocr = "\uE8FE";
    public const string Close = "\uE711";

    public const string Color = "\uE790";
    public const string Thickness = "\uEDA8";
    public const string Fill = "\uEA3B";
    public const string FontSize = "\uE8E9";
    public const string Eyedropper = "\uEF3C";

    public const string OpenFile = "\uE8E5";
    public const string CopyText = "\uE8FE";
    public const string ColorPicker = "\uEF3C";
    public const string Ruler = "\uED5E";
    public const string QrScan = "\uED14";

    public const string Edit = "\uE70F";
    public const string Open = "\uE8E5";
    public const string CopyPath = "\uE8C8";
    public const string Rename = "\uE8AC";
    public const string Favorite = "\uE734";
    public const string FavoriteFill = "\uE735";
    public const string Folder = "\uE8B7";
    public const string Delete = "\uE74D";
    public const string Check = "\uE73E";
    public const string Import = "\uE8B5";
    public const string Settings = "\uE713";
    public const string Info = "\uE946";

    public static string Pick(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).Glyph ?? "";

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
