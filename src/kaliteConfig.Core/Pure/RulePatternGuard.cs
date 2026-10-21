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
