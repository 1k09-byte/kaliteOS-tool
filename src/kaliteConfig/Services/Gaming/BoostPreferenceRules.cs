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

public static class BoostPreferenceRules
{
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

    public static IReadOnlyList<string> KeeperBoostApplyOrder { get; } =
        new[] { ProcessLayerName, ThreadLayerName };

    public const string ProcessLayerName = "process";
    public const string ThreadLayerName = "thread";

    public static async System.Threading.Tasks.Task ApplyBoostLayersInOrderAsync(
        System.Func<System.Threading.Tasks.Task> processLayer,
        System.Func<System.Threading.Tasks.Task> threadLayer)
    {
        await processLayer();
        await threadLayer();
    }
}
