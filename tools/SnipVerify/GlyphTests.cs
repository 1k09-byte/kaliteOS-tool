using System;
using System.Collections.Generic;
using System.IO;

// Verifies every PUA codepoint the Snip UI uses exists in Segoe MDL2 Assets
// (segmdl2.ttf), the guaranteed-present fallback behind SymbolThemeFontFamily.
// Meanings were verified against the Microsoft Learn glyph tables for both
// Segoe Fluent Icons and Segoe MDL2 Assets (Nov 2026 fetch).
internal static class GlyphTests
{
    // codepoint -> (where used, verified meaning)
    private static readonly (int Code, string Use, string Meaning)[] Required =
    {
        (0xE8B0, "overlay Select tool", "Click"),
        (0xE72A, "overlay Arrow tool", "Forward"),
        (0xE738, "overlay Line tool", "Remove"),
        (0xE73A, "overlay Rectangle tool", "CheckboxComposite"),
        (0xEA3A, "overlay Ellipse tool", "CircleRing"),
        (0xED63, "overlay Pen tool", "Pencil"),
        (0xED64, "overlay Highlighter", "Marker"),
        (0xE8D2, "overlay Text tool", "Font"),
        (0xEC1B, "overlay Number badge", "Badge"),
        (0xE8F8, "overlay Blur tool", "BlockContact"),
        (0xE890, "overlay Spotlight", "View"),
        (0xE7A7, "overlay Undo", "Undo"),
        (0xE7A6, "overlay Redo", "Redo"),
        (0xE8FE, "overlay OCR", "Scan"),
        (0xE718, "overlay Pin", "Pin"),
        (0xE74E, "overlay Save", "Save"),
        (0xE8C8, "overlay Copy/Instant", "Copy"),
        (0xE711, "overlay Close", "Cancel"),
        (0xE73E, "overlay instant badge", "CheckMark"),
        (0xEA3B, "overlay Fill toggle", "CircleFill"),
        (0xEF3C, "overlay Custom color", "Eyedropper"),
        (0xE7A8, "tile Region", "Crop"),
        (0xE8A7, "tile Window", "OpenInNewWindow"),
        (0xE740, "tile Fullscreen", "FullScreen"),
        (0xE7C9, "tile Freeform", "TouchPointer"),
        (0xE916, "tile Delayed", "Stopwatch"),
        (0xE7AD, "tile Repeat Last", "Rotate"),
        (0xE77F, "tile Clipboard", "Paste"),
    };

    public static void Run(Action<bool, string> check)
    {
        var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segmdl2.ttf");
        check(File.Exists(fonts), "glyph setup: segmdl2.ttf found");
        if (!File.Exists(fonts)) return;
        var covered = ParseCmap4(File.ReadAllBytes(fonts));
        foreach (var (code, use, meaning) in Required)
            check(covered.Contains(code), $"glyph U+{code:X4} ({meaning}) present for {use}");
    }

    // Minimal TrueType cmap format-4 reader (BMP only; all our codepoints are E000-F8FF).
    private static HashSet<int> ParseCmap4(byte[] font)
    {
        var covered = new HashSet<int>();
        int numTables = (font[4] << 8) | font[5];
        int cmapOff = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (font[rec] == 'c' && font[rec + 1] == 'm' && font[rec + 2] == 'a' && font[rec + 3] == 'p')
            {
                cmapOff = (font[rec + 8] << 24) | (font[rec + 9] << 16) | (font[rec + 10] << 8) | font[rec + 11];
                break;
            }
        }
        if (cmapOff < 0) return covered;
        int nSubs = (font[cmapOff + 2] << 8) | font[cmapOff + 3];
        for (int s = 0; s < nSubs; s++)
        {
            int sub = cmapOff + 4 + s * 8;
            int off = (font[sub + 4] << 24) | (font[sub + 5] << 16) | (font[sub + 6] << 8) | font[sub + 7];
            int t = cmapOff + off;
            int fmt = (font[t] << 8) | font[t + 1];
            if (fmt != 4) continue;
            int segX2 = (font[t + 6] << 8) | font[t + 7];
            int segCount = segX2 / 2;
            int endBase = t + 14, startBase = endBase + 2 + segCount * 2, deltaBase = startBase + segCount * 2, rangeBase = deltaBase + segCount * 2;
            for (int i = 0; i < segCount; i++)
            {
                int end = (font[endBase + i * 2] << 8) | font[endBase + i * 2 + 1];
                int start = (font[startBase + i * 2] << 8) | font[startBase + i * 2 + 1];
                int delta = (font[deltaBase + i * 2] << 8) | font[deltaBase + i * 2 + 1];
                int rangeOff = (font[rangeBase + i * 2] << 8) | font[rangeBase + i * 2 + 1];
                for (int c = start; c <= end && c <= 0xFFFF; c++)
                {
                    if (c == 0xFFFF) break;
                    int glyph;
                    if (rangeOff == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        int addr = rangeBase + i * 2 + rangeOff + 2 * (c - start);
                        glyph = ((font[addr] << 8) | font[addr + 1]);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph != 0) covered.Add(c);
                }
            }
        }
        return covered;
    }
}
