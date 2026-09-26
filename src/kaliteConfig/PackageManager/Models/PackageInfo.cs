// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.PackageManager.Models;

/// <summary>
/// One package row, regardless of backend. Versions are raw vendor strings
/// (winget emits oddities like "&lt; 3.14.7") - never parsed here.
/// </summary>
public sealed partial class PackageInfo : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string SourceId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SourceLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string Publisher { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    public partial string InstalledVersion { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    public partial string AvailableVersion { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Live operation on this row, if any (progress UI binds here).</summary>
    [ObservableProperty] public partial PackageOperationStatus? ActiveOperation { get; set; }

    /// <summary>
    /// True when an update is known: an available version that differs from
    /// installed. Deliberately string-based - no numeric parsing of vendor
    /// version soup.
    /// </summary>
    public bool HasUpdate =>
        !string.IsNullOrWhiteSpace(AvailableVersion)
        && !string.Equals(AvailableVersion.Trim(), (InstalledVersion ?? "").Trim(),
            System.StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string VersionLine
    {
        get
        {
            bool hasInstalled = !string.IsNullOrWhiteSpace(InstalledVersion);
            bool hasAvailable = !string.IsNullOrWhiteSpace(AvailableVersion);
            if (hasInstalled && hasAvailable)
                return string.Equals(InstalledVersion.Trim(), AvailableVersion.Trim(),
                    System.StringComparison.OrdinalIgnoreCase)
                    ? InstalledVersion.Trim()
                    : $"{InstalledVersion.Trim()} → {AvailableVersion.Trim()}";
            if (hasAvailable) return AvailableVersion.Trim();
            if (hasInstalled) return InstalledVersion.Trim();
            return "-";
        }
    }
}
