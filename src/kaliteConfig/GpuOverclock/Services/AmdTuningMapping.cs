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
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    internal static class AmdTuningMapping
    {
        internal static bool ReportsOffsetSemantics(AdlxIntRange range) => range.MinValue <= 0;

        internal static OverclockControlRange ToControlRange(AdlxIntRange range) =>
            new(range.MinValue, range.MaxValue, Math.Max(1, range.Step));

        internal static OverclockControlRange ToOffsetControlRange(AdlxIntRange absolute, int baseline) =>
            new(absolute.MinValue - baseline, absolute.MaxValue - baseline, Math.Max(1, absolute.Step));

        internal static int ToAbsolute(int baseline, int offset) => baseline + offset;

        internal static int ToOffset(int baseline, int absolute) => absolute - baseline;

        internal static int ClampToRange(AdlxIntRange range, int value)
        {
            int step = range.Step > 0 ? range.Step : 1;
            int snapped = range.MinValue + ((value - range.MinValue) / step) * step;
            return Math.Clamp(snapped, range.MinValue, range.MaxValue);
        }

        internal static bool WithinRange(AdlxIntRange range, int value) =>
            value >= range.MinValue && value <= range.MaxValue;
    }
}
