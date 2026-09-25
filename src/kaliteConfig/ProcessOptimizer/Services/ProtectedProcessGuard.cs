using System;
using System.Collections.Generic;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ProtectedProcessGuard
{
    /// <summary>
    /// Processes that are absolutely critical for Windows stability, 
    /// security frameworks, or the tuner application itself.
    /// </summary>
    private static readonly HashSet<string> _hardDenylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "services", "lsass", "winlogon", "smss", "system", "idle", "registry",
        "dwm", "audiodg", "spoolsv", "fontdrvhost", "sihost", "taskhostw",
        // Security software
        "msmpeng", "nissrv", "smartscreen", "sechealthui",
        // Application itself
        "kaliteconfig"
    };

    public static bool IsProcessProtected(string processName, IEnumerable<string> customExclusions = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true; // Err on side of caution

        string name = processName.ToLowerInvariant();

        // Callers are inconsistent about the extension: some pass
        // "proc.ProcessName" ("dwm") and some "proc.ProcessName + \".exe\""
        // ("dwm.exe"). The denylist is spelled WITHOUT it, so normalise here -
        // otherwise any caller that appends ".exe" silently disables the entire
        // hard denylist and the processes it exists to protect become fair game.
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        
        if (_hardDenylist.Contains(name))
            return true;
            
        if (customExclusions != null)
        {
            foreach (var exclusion in customExclusions)
            {
                if (string.IsNullOrWhiteSpace(exclusion)) continue;
                if (name.Contains(exclusion.ToLowerInvariant()))
                    return true;
            }
        }
        
        return false;
    }
}
