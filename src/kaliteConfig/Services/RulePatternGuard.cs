// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
namespace kaliteConfig.Services;

/// <summary>
/// Guards a saved rule's process pattern.
///
/// A pattern is an executable token ("dwm.exe", "cs2*"); a rule name is a
/// sentence for humans. When the two are identical the rule can never match a
/// process, so it sits at "Waiting for process" forever. That is exactly how
/// the shipped Process Control rule stopped applying: the saved copy carried
/// <c>Pattern = "Input/Sensor Threads"</c> instead of <c>"dwm.exe"</c>, so the
/// DWM Master Input / Kernel Sensor thread rules never ran.
/// </summary>
public static class RulePatternGuard
{
    /// <summary>True when a rule uses its own display name as the process pattern.</summary>
    public static bool IsRuleNameUsedAsPattern(string? pattern, string? name) =>
        !string.IsNullOrWhiteSpace(pattern)
        && !string.IsNullOrWhiteSpace(name)
        && string.Equals(pattern.Trim(), name.Trim(), System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pattern a built-in rule should carry, or null when nothing needs
    /// repairing: an unknown rule (no shipped pattern to fall back on), a rule
    /// whose pattern is already the shipped one, or a rule whose pattern is a
    /// real executable token the user chose.
    /// </summary>
    public static string? RepairPattern(string? pattern, string? name, string? shippedPattern)
    {
        if (string.IsNullOrWhiteSpace(shippedPattern)) return null;
        if (string.Equals(pattern?.Trim(), shippedPattern.Trim(), System.StringComparison.OrdinalIgnoreCase)) return null;
        return IsRuleNameUsedAsPattern(pattern, name) ? shippedPattern : null;
    }
}
