using System;
using System.IO;
using System.Text.Json;

namespace kaliteConfig.Services;

public class SnipSettings
{
    public bool InstantMode { get; set; } = false;
    public string ActiveTool { get; set; } = "";
    public float Thickness { get; set; } = 4f;
    public string ColorHex { get; set; } = "#FFFF0000";
    public int HotkeyIndex { get; set; } = 0;
    public double GalleryThumbSize { get; set; } = 220;
    public int AutoDeleteDays { get; set; } = 0;
    public int DelayedCaptureSeconds { get; set; } = 3;
}

public static class SnipSettingsService
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "snip-settings.json");

    public static SnipSettings Load()
    {
        try 
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<SnipSettings>(File.ReadAllText(SettingsPath)) ?? new SnipSettings();
        } 
        catch { }
        return new SnipSettings();
    }

    public static void Save(SnipSettings settings)
    {
        try 
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        } 
        catch { }
    }
}
