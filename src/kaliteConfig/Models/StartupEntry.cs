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
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models;

/// <summary>Which startup mechanism an entry belongs to.</summary>
public enum StartupEntryKind
{
    RunUser,
    RunMachine,
    Service,
    ScheduledTask,
}

/// <summary>
/// One startup entry: a Run registry value, a user-mode service, or a
/// non-Microsoft scheduled task. IsEnabled is two-way bound to the row
/// checkbox - the ViewModel commits the flip through the service.
/// </summary>
public sealed partial class StartupEntry : ObservableObject
{
    [ObservableProperty] public partial StartupEntryKind Kind { get; set; }

    /// <summary>Stable key: value name / service name / task path.</summary>
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;

    /// <summary>Display name (service display name, task short name, value name).</summary>
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    /// <summary>Type column: "" for Run values, start mode for services, trigger kind for tasks.</summary>
    [ObservableProperty] public partial string EntryType { get; set; } = string.Empty;

    /// <summary>Command / value column.</summary>
    [ObservableProperty] public partial string Command { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    /// <summary>Binary company for services (Microsoft-hide filter); empty otherwise.</summary>
    public string Company { get; set; } = string.Empty;
}
