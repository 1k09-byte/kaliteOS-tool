// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
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
