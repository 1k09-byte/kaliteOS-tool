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
    /// <summary>User-captured hotkey (used when HotkeyIndex == SnipHotkeyService.CustomIndex).</summary>
    public uint CustomHotkeyModifiers { get; set; } = SnipHotkeyService.ModControl | SnipHotkeyService.ModShift;
    /// <summary>Virtual key of the user-captured hotkey.</summary>
    public uint CustomHotkeyVk { get; set; } = SnipHotkeyService.VkS;
    public double GalleryThumbSize { get; set; } = 220;
    /// <summary>Fill card mode: cover (crops) instead of fit (never crops). Off by default.</summary>
    public bool ThumbnailFillMode { get; set; } = false;
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
