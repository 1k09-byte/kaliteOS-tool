// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System.Collections.Generic;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Which dynamic ranges the driver will accept for a given colour format.
///
/// The panel presents colour format and dynamic range as two independent controls, but the
/// driver does not treat them independently: YCbCr is a limited-range signal, so every
/// subsampling mode is rejected outright when paired with full range. Offering that
/// pairing and then reporting "unsupported" after the fact is how the controls ended up
/// feeling broken, so <see cref="ReachableRanges"/> is the single source of truth the
/// panel trims itself against.
/// </summary>
public static class NvidiaColorMap
{
    /// <summary>
    /// The dynamic ranges the driver accepts for <paramref name="format"/>, in list order.
    /// </summary>
    public static IReadOnlyList<NvidiaDynamicRangeOption> ReachableRanges(NvidiaColorFormatOption format) => format switch
    {
        NvidiaColorFormatOption.Rgb => new[]
        {
            NvidiaDynamicRangeOption.Full,
            NvidiaDynamicRangeOption.Limited,
        },
        _ => new[] { NvidiaDynamicRangeOption.Limited },
    };

    /// <summary>
    /// Keeps <paramref name="desired"/> when <paramref name="format"/> allows it, otherwise
    /// takes the first range that format does allow. Picking a YCbCr mode while the panel is
    /// on full range has to move the range rather than leave an unreachable value in place.
    /// </summary>
    public static NvidiaDynamicRangeOption ReachableRangeFor(NvidiaColorFormatOption format, NvidiaDynamicRangeOption desired)
    {
        var reachable = ReachableRanges(format);
        foreach (var option in reachable)
            if (option == desired) return desired;
        return reachable[0];
    }

    /// <summary>
    /// True when the driver's own combination check should accept this pairing. Kept here so
    /// the same rule that trims the panel also explains a rejection in words the user can act
    /// on.
    /// </summary>
    public static bool IsReachable(NvidiaColorFormatOption format, NvidiaDynamicRangeOption range) =>
        ReachableRangeFor(format, range) == range;
}
