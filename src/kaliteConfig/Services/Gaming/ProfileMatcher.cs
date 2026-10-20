using System;

namespace kaliteConfig.Services;

public static class ProfileMatcher
{
    public static bool Matches(string pattern, string processName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(processName))
            return false;

        foreach (var token in pattern.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MatchesSingle(token, processName))
                return true;
        }
        return false;
    }

    private static bool MatchesSingle(string pattern, string processName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(processName))
            return false;
        if (pattern == "*")
            return true;

        string p = pattern;
        string n = processName;
        if (string.Equals(p, n, StringComparison.OrdinalIgnoreCase))
            return true;
        if (SimpleMatch(p, n))
            return true;

        string pNoExe = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
        string nNoExe = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
        if (string.Equals(pNoExe, nNoExe, StringComparison.OrdinalIgnoreCase))
            return true;
        if (SimpleMatch(pNoExe, nNoExe))
            return true;
        if (SimpleMatch(pNoExe, n))
            return true;
        if (SimpleMatch(p, nNoExe))
            return true;

        if (pNoExe.Contains('*') || nNoExe.Length == 0) return false;
        return nNoExe.StartsWith(pNoExe, StringComparison.OrdinalIgnoreCase)
            && nNoExe.Length > pNoExe.Length;
    }

    private static bool SimpleMatch(string pattern, string input)
    {
        if (pattern.StartsWith("*") && pattern.EndsWith("*") && pattern.Length > 1)
        {
            return input.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.StartsWith("*"))
        {
            return input.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.EndsWith("*"))
        {
            return input.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
