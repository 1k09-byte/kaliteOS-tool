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

namespace kaliteConfig.Services;

public sealed class SnipRefreshCadence
{
    public TimeSpan Burst { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackedOff { get; init; } = TimeSpan.FromSeconds(20);

    public int BackOffAfter { get; init; } = 6;

    public int UnchangedStreak { get; private set; }

    public TimeSpan Current { get; private set; }

    public SnipRefreshCadence() => Current = Idle;

    public TimeSpan Observe(bool folderChanged)
    {
        if (folderChanged)
        {
            UnchangedStreak = 0;
            Current = Burst;
            return Current;
        }

        UnchangedStreak++;
        Current = UnchangedStreak >= BackOffAfter ? BackedOff : Idle;
        return Current;
    }

    public TimeSpan Nudge()
    {
        UnchangedStreak = 0;
        Current = Burst;
        return Current;
    }
}
