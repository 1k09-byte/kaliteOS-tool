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
