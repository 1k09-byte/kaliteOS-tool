using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>
/// Turns an existing ordered list into a desired one with as few edits as possible, reusing the
/// instances that are already there.
///
/// This exists because a background refresh has to be invisible. Clearing the collection and
/// re-adding everything recycles every item container: skeletons flash back on, already decoded
/// thumbnails are thrown away, the scroll position jumps to the top and the selection (and with
/// it the details preview) is lost. Touching only the items that actually changed leaves every
/// card that is still present exactly as it is.
/// </summary>
public static class SilentListMerge
{
    /// <summary>What a merge did, so callers can decide whether anything needs repainting.</summary>
    public readonly record struct Result(int Removed, int Inserted, int Moved)
    {
        public bool Changed => Removed > 0 || Inserted > 0 || Moved > 0;
    }

    /// <summary>
    /// Rearranges <paramref name="target"/> in place to match <paramref name="desired"/>, matching
    /// items by <paramref name="key"/>. Items already in the target are kept (so their state
    /// survives); items that are new come from <paramref name="desired"/>; items that are gone are
    /// removed. Removal runs back-to-front so the indices stay valid while walking.
    /// </summary>
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
