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

namespace kaliteConfig.Services;

/// <summary>
/// Processes that must NEVER be demoted/eco'd/swept by Gaming mode, beyond
/// the hard-critical list. Three tiers:
/// - Audio/capture (EcoQoS on OBS drops frames, on Discord it lags voice)
/// - Launchers + overlays + anti-cheat (break games or trip anti-tamper)
/// - User whitelist (edit %LocalAppData%/kaliteConfig/gaming-exempt.txt,
///   one process name per line, # comments allowed; reloaded on every check)
/// </summary>
public static class GamingExemptionService
{
    private static readonly HashSet<string> AudioCapture = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "streamlabs", "streamlabsobs", "xsplit", "wirecast",
        "discord", "discordptb", "discordcanary", "teamspeak", "teamspeak3",
        "voicemeeter", "voicemeeterxbuttery", "audiodelay", "navigator",
        "steam", "steamwebhelper", "gedit",  // Steam voice/overlay host
    };

    private static readonly HashSet<string> LaunchersOverlaysAnticheat = new(StringComparer.OrdinalIgnoreCase)
    {
        // Launchers
        "epicgameslauncher", "origin", "eadesktop", "ubisoftconnect", "upc",
        "battlenet", "agent", "riotclientservices", "launcher", "galaxyclient",
        "gog galaxy", "xbox", "xboxapp", "gamebar", "wtss",
        // Overlays
        "rtss", "afterburner", "msiafterburner", "rivatuner",
        "nvidia web helper", "nvidias_share", "nvcontainer", "geforceexperience",
        "amdow", "amdrsserv", "radeonsettings", "adrenalin",
        // Anti-cheat / anti-tamper
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

    /// <summary>True when the process name must not be touched by gaming mode.</summary>
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

    /// <summary>Creates a commented starter whitelist file if none exists.</summary>
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
