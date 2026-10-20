using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace kaliteConfig.Services;

public static class GamingExemptionService
{
    private static readonly HashSet<string> AudioCapture = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "streamlabs", "streamlabsobs", "xsplit", "wirecast",
        "discord", "discordptb", "discordcanary", "teamspeak", "teamspeak3",
        "voicemeeter", "voicemeeterxbuttery", "audiodelay", "navigator",
        "steam", "steamwebhelper", "gedit",
    };

    private static readonly HashSet<string> LaunchersOverlaysAnticheat = new(StringComparer.OrdinalIgnoreCase)
    {
        "epicgameslauncher", "origin", "eadesktop", "ubisoftconnect", "upc",
        "battlenet", "agent", "riotclientservices", "launcher", "galaxyclient",
        "gog galaxy", "xbox", "xboxapp", "gamebar", "wtss",
        "rtss", "afterburner", "msiafterburner", "rivatuner",
        "nvidia web helper", "nvidias_share", "nvcontainer", "geforceexperience",
        "amdow", "amdrsserv", "radeonsettings", "adrenalin",
        "easyanticheat", "easyanticheat_exe", "beservice", "battleye",
        "battleyeservice", "vgc", "vgtray", "faceitclient", "faceit",
        "ese", "protected", "sentinel", "cpbrick", "anticheat",
        " eupbrothers", "cod22-cod", "msteams",
    };

    private static readonly object _gate = new();
    private static DateTime _whitelistAge = DateTime.MinValue;
    private static HashSet<string> _whitelist = new(StringComparer.OrdinalIgnoreCase);

    private static string WhitelistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "gaming-exempt.txt");

    public static bool IsExempt(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        string bare = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        lock (_gate)
        {
            if ((DateTime.UtcNow - _whitelistAge).TotalSeconds > 15)
            {
                _whitelistAge = DateTime.UtcNow;
                try
                {
                    if (File.Exists(WhitelistPath))
                    {
                        _whitelist = File.ReadAllLines(WhitelistPath)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { }
            }
            return AudioCapture.Contains(bare)
                || LaunchersOverlaysAnticheat.Contains(bare)
                || _whitelist.Contains(bare);
        }
    }

    public static void EnsureStarterFile()
    {
        try
        {
            string path = WhitelistPath;
            if (File.Exists(path)) return;
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, new[]
            {
                "# Gaming mode will never touch processes listed here (one per line).",
                "# Use the process name without .exe. Lines starting with # are comments.",
                "# Example:",
                "# myoverlayservice",
                "# voicemeeter",
            });
        }
        catch { }
    }
}
