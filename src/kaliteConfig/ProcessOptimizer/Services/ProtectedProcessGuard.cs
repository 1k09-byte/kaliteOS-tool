using System;
using System.Collections.Generic;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ProtectedProcessGuard
{
    private static readonly HashSet<string> _hardDenylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "services", "lsass", "winlogon", "smss", "system", "idle", "registry",
        "dwm", "audiodg", "spoolsv", "fontdrvhost", "sihost", "taskhostw",
        "msmpeng", "nissrv", "smartscreen", "sechealthui",
        "kaliteconfig"
    };

    public static bool IsProcessProtected(string processName, IEnumerable<string>? customExclusions = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;

        string name = processName.ToLowerInvariant();

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
