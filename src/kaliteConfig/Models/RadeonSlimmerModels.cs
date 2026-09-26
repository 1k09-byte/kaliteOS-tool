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
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models
{
    public enum RadeonPackageCategory
    {
        Driver,
        Application,
        Telemetry,
        Utility
    }

    public enum SlimmerPreset
    {
        DisplayOnly,
        LowLatencyGaming,
        FullExperience,
        Custom
    }

    /// <summary>
    /// Package entry matching RadeonSoftwareSlimmer Tab 1: Packages (Drivers & MSIs)
    /// </summary>
    public partial class RadeonPackageItem : ObservableObject
    {
        public string Id { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Name { get => ProductName; set => ProductName = value; }
        public string LocationUrl { get; set; } = string.Empty;
        public string PackageType { get; set; } = "DRIVER";
        public string Description { get; set; } = string.Empty;
        public RadeonPackageCategory Category { get; set; } = RadeonPackageCategory.Driver;
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }

        public string FormattedSize => SizeBytes > 0
            ? $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
            : string.Empty;

        public bool IsRequired { get; set; }
        public bool IsRemovable => !IsRequired;

        [ObservableProperty]
        public partial bool IsSelected { get; set; } = true;
    }

    /// <summary>
    /// Scheduled Task entry matching RadeonSoftwareSlimmer Tab 2: Scheduled Tasks
    /// </summary>
    public partial class RadeonScheduledTaskItem : ObservableObject
    {
        public string Uri { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsTelemetry { get; set; }

        [ObservableProperty]
        public partial bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Display Driver Component matching RadeonSoftwareSlimmer Tab 3: Display Driver Components
    /// </summary>
    public partial class RadeonDisplayComponentItem : ObservableObject
    {
        public string Directory { get; set; } = string.Empty;
        public string InfFile { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
        public bool IsRemovable => !IsRequired;
        public bool IsTelemetry { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; } = true;
    }

}
