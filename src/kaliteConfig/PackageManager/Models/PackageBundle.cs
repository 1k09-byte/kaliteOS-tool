using System;
using System.Collections.Generic;

namespace kaliteConfig.PackageManager.Models;

public sealed class BundleItem
{
    public string PackageId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class PackageBundle
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<BundleItem> Items { get; set; } = new();
}
