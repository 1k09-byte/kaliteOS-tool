namespace kaliteConfig.PackageManager.Models;

/// <summary>
/// Ignored update: skip this package (optionally only one version).
/// Model ships now; the manage-ignored-updates UI lands after the core
/// flows are solid (process step 4).
/// </summary>
public sealed class IgnoredUpdateEntry
{
    public string PackageId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Version to skip, or "all".</summary>
    public string Scope { get; set; } = "all";
}
