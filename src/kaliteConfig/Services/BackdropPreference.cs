using System;
using System.IO;

namespace kaliteConfig.Services;

internal static class BackdropPreference
{
    private static string MarkerPath(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", name);

    private static bool Chosen(string marker)
    {
        try { return File.Exists(MarkerPath(marker)); }
        catch { return true; }
    }

    private static void SetChosen(string marker, bool value)
    {
        try
        {
            string path = MarkerPath(marker);
            if (value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch {  }
    }

    public static bool MaterialChosen
    {
        get => Chosen("material-chosen.marker");
        set => SetChosen("material-chosen.marker", value);
    }
}
