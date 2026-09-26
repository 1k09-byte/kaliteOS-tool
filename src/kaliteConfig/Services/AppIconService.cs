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
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace kaliteConfig.Services;

/// <summary>
/// Loads real per-app icons: Win32 via the shell thumbnail of the exe/DisplayIcon
/// target (or an exe found in the install folder), AppX via the package logo file.
/// Thumbnails are cached to %LocalAppData%\kaliteConfig\IconCache so repeat visits
/// are instant. Everything returns null on failure - callers keep the letter-avatar
/// fallback. Must be called on the UI thread (BitmapImage requires it).
/// </summary>
public static class AppIconService
{
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "IconCache");

    private static readonly string[] NoiseExeNames =
        { "uninstall", "unins", "setup", "update", "installer", "helper", "crash", "report" };

    public static string? ResolveCandidate(Models.UninstallerItem item)
    {
        return CleanPath(item.DisplayIcon)
            ?? CleanPath(item.UninstallString)
            ?? FindExeInInstallLocation(item);
    }

    private static string? FindExeInInstallLocation(Models.UninstallerItem item)
    {
        var loc = item.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(loc)) return null;
        try
        {
            if (File.Exists(loc)) return loc; // points straight at a file
            if (!Directory.Exists(loc)) return null;
            var exes = Directory.GetFiles(loc, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length == 0) return null;
            var usable = exes.Where(e => !NoiseExeNames.Any(n =>
                Path.GetFileNameWithoutExtension(e).Contains(n, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (usable.Length == 0) usable = exes;
            if (usable.Length == 1) return usable[0];
            // Prefer an exe whose name shares a token with the app name.
            var tokens = item.Name.Split(new[] { ' ', '-', '_', '(', ')', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2).Select(t => t.ToLowerInvariant()).ToArray();
            return usable.FirstOrDefault(e =>
                    tokens.Any(t => Path.GetFileNameWithoutExtension(e).Contains(t, StringComparison.OrdinalIgnoreCase)))
                ?? usable[0];
        }
        catch { return null; }
    }

    public static string? CleanPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"');
        // DisplayIcon often looks like "C:\...\\app.exe,0" - drop the ",index".
        int comma = s.LastIndexOf(',');
        if (comma > 0)
        {
            var tail = s.Substring(comma + 1).Trim();
            if (int.TryParse(tail, out _)) s = s.Substring(0, comma).Trim().Trim('"');
        }
        // Uninstall strings carry arguments - keep only a bare existing exe path.
        if (!File.Exists(s))
        {
            if (s.StartsWith("\""))
            {
                int q = s.IndexOf("\"", 1);
                if (q > 0) s = s.Substring(1, q - 1);
            }
            else
            {
                int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx > 0) s = s.Substring(0, exeIdx + 4).Trim().Trim('"');
            }
        }
        if (File.Exists(s)) return s;
        if (s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(s)) return s;
        }
        return null;
    }

    public static async Task<BitmapImage?> TryGetIconAsync(string? candidatePath)
    {
        var path = CleanPath(candidatePath);
        if (path == null) return null;
        try
        {
            // Raw images load directly.
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return new BitmapImage(new Uri(path));
            }
            // Disk cache: key = stable path hash + last-write time.
            var info = new FileInfo(path);
            string key = $"{StableHash(path):x8}_{info.LastWriteTimeUtc.Ticks:x16}.thumb";
            string cached = Path.Combine(CacheDir, key);
            if (File.Exists(cached)) return new BitmapImage(new Uri(cached));

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale);
            if (thumb == null || thumb.Size == 0) return null;

            // Persist the raw thumbnail bytes (already a valid image) for next time.
            try
            {
                var buf = new Windows.Storage.Streams.Buffer((uint)thumb.Size);
                await thumb.ReadAsync(buf, (uint)thumb.Size, InputStreamOptions.None);
                byte[] bytes = new byte[buf.Length];
                using (var reader = DataReader.FromBuffer(buf)) reader.ReadBytes(bytes);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(cached, bytes);
                return new BitmapImage(new Uri(cached));
            }
            catch
            {
                // Cache write failed - still show this session's icon.
                var bmp = new BitmapImage();
                thumb.Seek(0);
                await bmp.SetSourceAsync(thumb);
                return bmp;
            }
        }
        catch { return null; }
    }

    private static uint StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return h;
        }
    }
}
