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

namespace kaliteConfig.Models
{
    /// <summary>
    /// Single tracked registry modification - used by the undo/redo stack
    /// and the "View Changes" dialog.
    /// </summary>
    public sealed record AffinityChange(
        string DeviceId,
        string DeviceName,
        string PropertyName,   // "MsiEnabled", "MessageNumberLimit", "DevicePolicy", "DevicePriority", "AffinityMask"
        object? OldValue,
        object? NewValue,
        DateTime Timestamp);
}
