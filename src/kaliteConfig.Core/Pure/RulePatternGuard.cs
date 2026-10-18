// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
namespace kaliteConfig.Services;

public static class RulePatternGuard
{
    public static bool IsRuleNameUsedAsPattern(string? pattern, string? name) =>
        !string.IsNullOrWhiteSpace(pattern)
        && !string.IsNullOrWhiteSpace(name)
        && string.Equals(pattern.Trim(), name.Trim(), System.StringComparison.OrdinalIgnoreCase);

    public static string? RepairPattern(string? pattern, string? name, string? shippedPattern)
    {
        if (string.IsNullOrWhiteSpace(shippedPattern)) return null;
        if (string.Equals(pattern?.Trim(), shippedPattern.Trim(), System.StringComparison.OrdinalIgnoreCase)) return null;
        return IsRuleNameUsedAsPattern(pattern, name) ? shippedPattern : null;
    }
}
