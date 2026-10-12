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
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>
/// The identity-matching and "which state wins" rules behind
/// <see cref="BoostPreferenceService"/>, split out so they can be tested
/// without the WinUI application object or a live thread handle.
/// </summary>
public static class BoostPreferenceRules
{
    /// <summary>
    /// True when a stored preference refers to the same logical thread as the
    /// supplied description / start address. Threads have no stable TID across
    /// restarts, so identity is the description when it has one and the start
    /// address otherwise. An unnamed, address-less thread matches nothing - it
    /// has no identity to match on, and guessing would let one thread's choice
    /// leak onto every other unnamed thread in the process.
    /// </summary>
    public static bool IdentityMatches(
        string storedDescription, string storedStartAddress,
        string description, string startAddress)
    {
        bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);

        if (HasText(storedDescription) && HasText(description) &&
            string.Equals(storedDescription, description, StringComparison.OrdinalIgnoreCase))
            return true;

        if (HasText(storedStartAddress) && HasText(startAddress) &&
            string.Equals(storedStartAddress, startAddress, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves the state a thread must be forced into, or null when the user
    /// never chose one and it should follow Windows.
    ///
    /// <paramref name="prefs"/> is the set of stored preferences that belong to
    /// THIS process; <paramref name="description"/>/<paramref name="startAddress"/>
    /// identify the live thread. A later entry wins over an earlier one, which
    /// is what the store already does when it replaces a matching record.
    /// </summary>
    public static bool? ResolveDesiredState(
        IEnumerable<(string Description, string StartAddress, bool Enabled)> prefs,
        string description, string startAddress)
    {
        bool? wanted = null;
        foreach (var p in prefs)
        {
            if (IdentityMatches(p.Description, p.StartAddress, description, startAddress))
                wanted = p.Enabled;
        }
        return wanted;
    }

    /// <summary>
    /// The order the keeper sweep must apply the two boost layers in.
    ///
    /// SetProcessPriorityBoost(disable: true) applies "disabled" to every thread
    /// that already exists in the process, so the process-wide layer has to run
    /// FIRST and the per-thread layer SECOND - otherwise every per-thread
    /// "boost on" is stomped off and (with nothing re-asserting it) never
    /// comes back. The other order is the bug this encodes as a test.
    /// </summary>
    public static IReadOnlyList<string> KeeperBoostApplyOrder { get; } =
        new[] { ProcessLayerName, ThreadLayerName };

    public const string ProcessLayerName = "process";
    public const string ThreadLayerName = "thread";

    /// <summary>
    /// Runs the process-wide boost layer and then the per-thread layer, in that
    /// order, awaiting each one to completion. Every caller that re-arms boost
    /// preferences goes through here so the ordering cannot be got wrong again.
    /// </summary>
    public static async System.Threading.Tasks.Task ApplyBoostLayersInOrderAsync(
        System.Func<System.Threading.Tasks.Task> processLayer,
        System.Func<System.Threading.Tasks.Task> threadLayer)
    {
        await processLayer();
        await threadLayer();
    }
}
