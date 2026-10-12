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
using kaliteConfig.Models;

namespace kaliteConfig.Services;

public static class SnipGalleryQuery
{
    public static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    public static SnipFormat FormatFromExtension(string ext) => SnipFormatLookup.FromExtension(ext);

    public static bool Matches(SnipEntry e, SnipFilterSpec spec, DateTime nowUtc)
    {
        if (spec.FavoritesOnly && !e.IsFavorite) return false;

        if (spec.Type != "All" && !string.Equals(e.Type, spec.Type, StringComparison.OrdinalIgnoreCase)) return false;

        if (spec.Format != "All")
        {
            var fmt = spec.Format.ToLowerInvariant();
            var ext = e.Format switch
            {
                SnipFormat.Png => "png",
                SnipFormat.Jpeg => "jpeg",
                SnipFormat.Bmp => "bmp",
                SnipFormat.Webp => "webp",
                _ => "",
            };
            if (!ext.StartsWith(fmt, StringComparison.Ordinal)) return false;
        }

        if (spec.SourceApp != "All" && !string.Equals(e.SourceApp, spec.SourceApp, StringComparison.OrdinalIgnoreCase)) return false;

        switch (spec.Date)
        {
            case SnipDateFilter.Today:
                if (e.CreatedUtc.Date != nowUtc.Date) return false;
                break;
            case SnipDateFilter.Last7:
                if (e.CreatedUtc < nowUtc.AddDays(-7)) return false;
                break;
            case SnipDateFilter.Last30:
                if (e.CreatedUtc < nowUtc.AddDays(-30)) return false;
                break;
            case SnipDateFilter.ThisMonth:
                if (e.CreatedUtc.Year != nowUtc.Year || e.CreatedUtc.Month != nowUtc.Month) return false;
                break;
        }

        if (!string.IsNullOrWhiteSpace(spec.Query))
        {
            var q = spec.Query.Trim().ToLowerInvariant();
            if (e.Name.ToLowerInvariant().Contains(q)) return true;
            if (e.SourceApp.ToLowerInvariant().Contains(q)) return true;
            if (e.OcrText.ToLowerInvariant().Contains(q)) return true;
            if (e.Type.ToLowerInvariant().Contains(q)) return true;
            if (e.Tags != null)
            {
                foreach (var t in e.Tags)
                {
                    if (t.ToLowerInvariant().Contains(q)) return true;
                }
            }
            return false;
        }

        return true;
    }

    public static void Sort(List<SnipEntry> entries, SnipSortMode mode)
    {
        switch (mode)
        {
            case SnipSortMode.Newest:
                entries.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
                break;
            case SnipSortMode.Oldest:
                entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
                break;
            case SnipSortMode.NameAsc:
                entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                break;
            case SnipSortMode.NameDesc:
                entries.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
                break;
            case SnipSortMode.SizeDesc:
                entries.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                break;
            case SnipSortMode.SizeAsc:
                entries.Sort((a, b) => a.SizeBytes.CompareTo(b.SizeBytes));
                break;
        }
    }

    public static List<SnipEntry> Apply(IEnumerable<SnipEntry> entries, SnipFilterSpec spec, SnipSortMode sort)
    {
        var nowUtc = DateTime.UtcNow;
        var result = new List<SnipEntry>();
        foreach (var e in entries)
        {
            if (Matches(e, spec, nowUtc)) result.Add(e);
        }
        Sort(result, sort);
        return result;
    }

    public static List<string> DistinctSourceApps(IEnumerable<SnipEntry> entries)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.SourceApp)) continue;
            if (seen.Add(e.SourceApp)) list.Add(e.SourceApp);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.00} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    public static string SafeFileName(string requested, string extension)
    {
        var name = requested.Trim();
        if (name.Length == 0) name = "Snip";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0) name = "Snip";
        if (System.IO.Path.GetExtension(name).ToLowerInvariant() != extension.ToLowerInvariant())
        {
            name += extension;
        }
        return name;
    }

    /// <summary>Resize target dimensions. Zero means "auto" for that axis.
    /// With aspect lock a single set axis scales both; both set fits inside.</summary>
    public static (int W, int H) ComputeResizeDims(int srcW, int srcH, int reqW, int reqH, bool aspectLock)
    {
        if (srcW <= 0 || srcH <= 0) return (0, 0);
        if (reqW <= 0 && reqH <= 0) return (0, 0);
        if (!aspectLock)
            return (reqW > 0 ? reqW : srcW, reqH > 0 ? reqH : srcH);
        double scale;
        if (reqW > 0 && reqH > 0) scale = Math.Min((double)reqW / srcW, (double)reqH / srcH);
        else if (reqW > 0) scale = (double)reqW / srcW;
        else scale = (double)reqH / srcH;
        if (scale <= 0) return (0, 0);
        return (Math.Max(1, (int)Math.Round(srcW * scale)), Math.Max(1, (int)Math.Round(srcH * scale)));
    }

    /// <summary>True when every sampled pixel is identical (DRM/protected content
    /// captures as one flat color; used to warn the user, never to block).</summary>
    public static bool IsUniformImage(byte[] bgra)
    {
        if (bgra == null || bgra.Length < 4) return true;
        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2], a0 = bgra[3];
        for (int i = 4; i < bgra.Length; i += 16) // sample every 4th BGRA pixel
        {
            if (bgra[i] != b0 || bgra[i + 1] != g0 || bgra[i + 2] != r0 || bgra[i + 3] != a0) return false;
        }
        return true;
    }

    /// <summary>True when the frame only LOOKS empty: at least <paramref name="fraction"/>
    /// of sampled pixels sit within <paramref name="tolerance"/> per channel of the first
    /// pixel. Catches near-flat captures (empty areas, JPEG noise, faint gradients) that
    /// exact-match misses. Photos of real content fail this.</summary>
    public static bool IsNearlyBlank(byte[] bgra, double fraction = 0.99, int tolerance = 12)
    {
        if (bgra == null || bgra.Length < 4) return true;
        if (fraction <= 0 || fraction > 1) fraction = 0.99;
        if (tolerance < 0) tolerance = 12;
        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2], a0 = bgra[3];
        long same = 0, total = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 16)
        {
            total++;
            if (Math.Abs(bgra[i] - b0) <= tolerance && Math.Abs(bgra[i + 1] - g0) <= tolerance &&
                Math.Abs(bgra[i + 2] - r0) <= tolerance && Math.Abs(bgra[i + 3] - a0) <= tolerance)
                same++;
        }
        return total > 0 && (double)same / total >= fraction;
    }
}