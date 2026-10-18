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

public static class SilentListMerge
{
    public readonly record struct Result(int Removed, int Inserted, int Moved)
    {
        public bool Changed => Removed > 0 || Inserted > 0 || Moved > 0;
    }

    public static Result Sync<T>(IList<T> target, IReadOnlyList<T> desired, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(key);

        var desiredKeys = new HashSet<string>(desired.Select(key), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var inserted = 0;
        var moved = 0;

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (desiredKeys.Contains(key(target[i]))) continue;
            target.RemoveAt(i);
            removed++;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = key(desired[i]);
            if (i < target.Count && string.Equals(key(target[i]), want, StringComparison.OrdinalIgnoreCase))
                continue;

            var present = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (string.Equals(key(target[j]), want, StringComparison.OrdinalIgnoreCase))
                {
                    present = j;
                    break;
                }
            }

            if (present >= 0)
            {
                var item = target[present];
                target.RemoveAt(present);
                if (i >= target.Count) target.Add(item);
                else target.Insert(i, item);
                moved++;
            }
            else
            {
                if (i >= target.Count) target.Add(desired[i]);
                else target.Insert(i, desired[i]);
                inserted++;
            }
        }

        return new Result(removed, inserted, moved);
    }
}
