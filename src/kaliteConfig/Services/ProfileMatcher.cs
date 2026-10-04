// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use, but the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;

namespace kaliteConfig.Services;

/// <summary>Shared rule-pattern matching (supports * wildcards, with/without .exe).
/// A pattern may list several processes separated by commas or semicolons
/// ("dwm.exe, csrss.exe") - any token matching wins. Previously the raw string
/// was compared as one pattern, so list-style rules never matched anything and
/// sat at "Waiting for process" even for always-running processes.</summary>
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

        // Tolerate .exe on either side: "DiscordPTB" == "DiscordPTB.exe",
        // "Discord*" matches "DiscordPTB.exe", "*potify" matches "fastpotify.exe".
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

        // A wildcard-free pattern is also a PREFIX. Without this a rule for
        // "fortnite" could only ever match a process named exactly "fortnite",
        // so the rule silently never applied to FortniteClient-Win64-Shipping and
        // the process kept whatever priority something else had left it at. People
        // type the beginning of a process name and expect it to work; the wildcards
        // above are still there when a rule really does need to be loose or exact.
        //
        // Prefix, not substring, on purpose: "Game" must not quietly start
        // matching XboxGameBar, and a substring rule would make a short pattern
        // match most of the machine.
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
