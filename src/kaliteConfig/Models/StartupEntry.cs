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

public enum StartupEntryKind
{
    RunUser,
    RunMachine,
    Service,
    ScheduledTask,
}

public sealed partial class StartupEntry : ObservableObject
{
    [ObservableProperty] public partial StartupEntryKind Kind { get; set; }

    [ObservableProperty] public partial string Id { get; set; } = string.Empty;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string EntryType { get; set; } = string.Empty;

    [ObservableProperty] public partial string Command { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    public string Company { get; set; } = string.Empty;
}
