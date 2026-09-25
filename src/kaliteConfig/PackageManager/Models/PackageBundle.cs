using System;
using System.Collections.Generic;

namespace kaliteConfig.PackageManager.Models;

/// <summary>One entry in a named bundle: which package, from which source.</summary>
public sealed class BundleItem
{
    public string PackageId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>Named, portable package set (JSON file per bundle on disk).</summary>
public sealed class PackageBundle
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<BundleItem> Items { get; set; } = new();
}
