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
