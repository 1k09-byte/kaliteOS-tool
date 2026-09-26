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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed record StickerEntry(string FilePath, string Name);

/// <summary>User-imported stickers for stamping onto captures. Lives outside the snips
/// gallery so stickers never show up as captures. System.IO only: no XAML objects,
/// so the import/list/delete round-trip is covered headless in SnipVerify.</summary>
public static class SnipStickerService
{
    public static string GetStickersDirectory()
    {
        // Same folder the capture overlay has always used for stickers.
        try
        {
            var local = Windows.Storage.ApplicationData.Current?.LocalFolder?.Path;
            if (!string.IsNullOrEmpty(local))
            {
                var dir = Path.Combine(local, "Stickers");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        catch { }
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Stickers");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static List<StickerEntry> GetAllStickers()
    {
        var list = new List<StickerEntry>();
        try
        {
            var dir = GetStickersDirectory();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                list.Add(new StickerEntry(file, Path.GetFileNameWithoutExtension(file)));
            }
        }
        catch { }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static string SanitizeStickerName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.TrimEnd('.', ' ');
        return name.Length == 0 ? "sticker" : name;
    }

    public static async Task<List<string>> ImportStickersAsync(IEnumerable<string> sourceFiles)
    {
        var imported = new List<string>();
        var dir = GetStickersDirectory();
        foreach (var src in sourceFiles)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;
                var ext = Path.GetExtension(src).ToLowerInvariant();
                if (!SnipGalleryQuery.SupportedExtensions.Contains(ext)) continue;
                var dest = Path.Combine(dir, SanitizeStickerName(src) + ext);
                var n = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(dir, $"{SanitizeStickerName(src)}_{n++}{ext}");
                await Task.Run(() => File.Copy(src, dest)).ConfigureAwait(false);
                imported.Add(dest);
            }
            catch { /* one bad file never blocks the rest; failures stay silent here and
                       the caller reports the count that actually landed */ }
        }
        return imported;
    }

    /// <summary>Deletes a sticker; returns false (and deletes nothing) when the path
    /// escapes the stickers folder.</summary>
    public static bool DeleteSticker(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var dir = Path.GetFullPath(GetStickersDirectory());
            var full = Path.GetFullPath(filePath);
            if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            if (File.Exists(full)) File.Delete(full);
            try { SnipThumbnailService.Invalidate(full); } catch { }
            return true;
        }
        catch { return false; }
    }
}
