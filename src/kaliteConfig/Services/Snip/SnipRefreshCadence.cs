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

/// <summary>
/// How often the snips page polls the folder, as a small state machine so the policy is
/// testable without a dispatcher timer.
///
/// The rule is: poll fast right after something changed (a capture lands as a short burst of
/// writes, and the user is looking at the gallery exactly then), then relax back to a lazy
/// interval once the folder has been quiet for a while. Because the poll itself is silent
/// (fingerprint first, merge-not-rebuild), the interval only trades latency for wakeups --
/// it never changes what the user sees.
/// </summary>
public sealed class SnipRefreshCadence
{
    /// <summary>Used for the ticks right after the folder changed.</summary>
    public TimeSpan Burst { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The steady-state interval.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Used once the folder has been unchanged for <see cref="BackOffAfter"/> ticks.</summary>
    public TimeSpan BackedOff { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Unchanged ticks in a row before backing off to <see cref="BackedOff"/>.</summary>
    public int BackOffAfter { get; init; } = 6;

    /// <summary>Unchanged ticks in a row. Reset by any change.</summary>
    public int UnchangedStreak { get; private set; }

    /// <summary>The delay to wait before the next poll.</summary>
    public TimeSpan Current { get; private set; }

    public SnipRefreshCadence() => Current = Idle;

    /// <summary>
    /// Records the outcome of one poll and returns the delay until the next one.
    /// </summary>
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

    /// <summary>
    /// Something happened that is likely to write snips (the window just came back from the
    /// capture overlay, a manual Refresh was pressed): tighten the cadence again so the burst
    /// window is open when the user is watching, even before the next poll proves a change.
    /// </summary>
    public TimeSpan Nudge()
    {
        UnchangedStreak = 0;
        Current = Burst;
        return Current;
    }
}
