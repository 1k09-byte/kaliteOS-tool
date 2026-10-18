using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace kaliteConfig.Services.LiveWallpaper;

public sealed class LiveWallpaperStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "live");

    public static string FilesDir => Path.Combine(RootDir, "files");
    public static string PackagesDir => Path.Combine(RootDir, "packages");
    public static string ThumbsDir => Path.Combine(RootDir, "thumbs");

    private static string SettingsPath => Path.Combine(RootDir, "settings.json");
    private static string LibraryPath => Path.Combine(RootDir, "library.json");

    public LiveSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var parsed = JsonSerializer.Deserialize<LiveSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (parsed is not null) return parsed;
            }
        }
        catch { }
        return LiveSettings.CreateDefault();
    }

    public void SaveSettings(LiveSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { }
    }

    public List<LiveWallpaperItem> LoadLibrary()
    {
        try
        {
            if (File.Exists(LibraryPath))
            {
                var parsed = JsonSerializer.Deserialize<List<LiveWallpaperItem>>(File.ReadAllText(LibraryPath), JsonOptions);
                if (parsed is not null) return parsed;
            }
        }
        catch { }
        return new List<LiveWallpaperItem>();
    }

    public void SaveLibrary(List<LiveWallpaperItem> items)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(items, JsonOptions));
        }
        catch { }
    }
}
