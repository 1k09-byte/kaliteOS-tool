using System;
using System.IO;

namespace kaliteConfig.Services;

internal static class NavLayoutPreference
{
    public const string RailTag = "Rail";
    public const string TopTag = "Top";
    public const string DockTag = "Bottom";
    public const string HiddenTag = "Hidden";

    private static string PrefPath(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", name);

    public static string Layout { get; private set; } = RailTag;
    public static bool StartOnHome { get; private set; } = true;

    static NavLayoutPreference()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            string layout = File.ReadAllText(PrefPath("nav-layout.txt")).Trim();
            if (layout == DockTag || layout == HiddenTag || layout == RailTag || layout == TopTag)
                Layout = layout;
        }
        catch { }
        try
        {
            string home = File.ReadAllText(PrefPath("start-home.txt")).Trim();
            if (home == "0") StartOnHome = false;
            else if (home == "1") StartOnHome = true;
        }
        catch { }
    }

    public static void SaveLayout(string tag)
    {
        if (tag != DockTag && tag != HiddenTag && tag != TopTag) tag = RailTag;
        Layout = tag;
        try
        {
            string path = PrefPath("nav-layout.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tag);
        }
        catch { }
    }

    public static void SaveStartOnHome(bool value)
    {
        StartOnHome = value;
        try
        {
            string path = PrefPath("start-home.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value ? "1" : "0");
        }
        catch { }
    }
}
