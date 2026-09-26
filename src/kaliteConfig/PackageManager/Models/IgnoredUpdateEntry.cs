// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
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
