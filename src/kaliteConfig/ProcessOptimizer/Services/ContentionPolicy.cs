using System;
using System.Collections.Generic;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

public static class ContentionPolicy
{
    public const double ContentionPercentOfTotalCpu = 3.0;

    public const int HotTicksBeforeThrottle = 2;

    public const int HotTicksToModerate = 4;

    public const int QuietTicksBeforeRelease = 5;

    public const int QuietTicksToForgetHeat = 2;

    public const double MinimumDwellSeconds = 15.0;

    public static double PercentOfTotalCpu(long deltaTicks, double elapsedMs, int cpuCount)
    {
        if (deltaTicks <= 0 || elapsedMs <= 0) return 0;
        return deltaTicks / 10_000.0 / elapsedMs * 100.0 / Math.Max(1, cpuCount);
    }

    public static bool IsContended(double percentOfTotalCpu)
        => percentOfTotalCpu >= ContentionPercentOfTotalCpu;

    public static ContentionSample ComputeTick(
        IReadOnlyDictionary<int, long> previous,
        IReadOnlyDictionary<int, long> current,
        double elapsedMs,
        int cpuCount)
    {
        var hot = new Dictionary<int, double>();
        var alive = new HashSet<int>(current.Keys);

        foreach (var kvp in current)
        {
            if (kvp.Key <= 4) continue;

            if (!previous.TryGetValue(kvp.Key, out long before)) continue;

            double percent = PercentOfTotalCpu(kvp.Value - before, elapsedMs, cpuCount);
            if (IsContended(percent)) hot[kvp.Key] = percent;
        }

        return new ContentionSample(hot, alive);
    }

    public static bool ShouldThrottle(int hotSamples)
        => hotSamples >= HotTicksBeforeThrottle;

    public static AggressivenessLevel EscalationFor(int hotSamples)
        => hotSamples >= HotTicksToModerate ? AggressivenessLevel.Moderate : AggressivenessLevel.Light;

    public static bool ShouldRelease(int quietTicks, double managedSeconds)
        => quietTicks >= QuietTicksBeforeRelease && managedSeconds >= MinimumDwellSeconds;

    public static bool ShouldForgetHeat(int quietTicks)
        => quietTicks >= QuietTicksToForgetHeat;
}
