using System;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    internal static class AmdTuningMapping
    {
        internal static bool ReportsOffsetSemantics(AdlxIntRange range) =>
            range.MinValue <= 0 && range.MaxValue <= 1000;

        internal static OverclockControlRange ToControlRange(AdlxIntRange range) =>
            new(range.MinValue, range.MaxValue, Math.Max(1, range.Step));

        internal static OverclockControlRange ToOffsetControlRange(AdlxIntRange absolute, int baseline) =>
            new(absolute.MinValue - baseline, absolute.MaxValue - baseline, Math.Max(1, absolute.Step));

        internal static AdlxIntRange ToOffsetRange(AdlxIntRange absolute, int baseline) => new()
        {
            MinValue = absolute.MinValue - baseline,
            MaxValue = absolute.MaxValue - baseline,
            Step = absolute.Step,
        };

        internal static AdlxIntRange ClampOffsetRange(AdlxIntRange range, int limitMhz) => new()
        {
            MinValue = Math.Max(range.MinValue, -limitMhz),
            MaxValue = Math.Min(range.MaxValue, limitMhz),
            Step = range.Step,
        };

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
