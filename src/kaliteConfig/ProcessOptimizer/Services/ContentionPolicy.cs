// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

/// <summary>
/// "Is this process competing with the game, and when do we stop punishing it."
///
/// Pure, so the thresholds have exactly one home and can be tested. Every value
/// here used to be inline: the percentage lived in the monitor, the escalation
/// ladder in the orchestrator, and the release rule was "it missed one sample",
/// which is what made demotion/restoration thrash.
///
/// The shape of the policy is deliberately asymmetric. It takes SUSTAINED heat
/// to apply anything (never punish the first busy sample) and SUSTAINED calm
/// plus a minimum dwell to give it back - because a demotion is a handful of
/// writes, but a restore is a full priority/eco/memory/IO pass, and doing that
/// repeatedly costs more than the contention being reacted to.
/// </summary>
public static class ContentionPolicy
{
    /// <summary>
    /// Share of TOTAL machine CPU (summed over all logical processors) that
    /// counts as contention. 3% of a 16-thread CPU is about half of one core
    /// held continuously.
    /// </summary>
    public const double ContentionPercentOfTotalCpu = 3.0;

    /// <summary>Consecutive hot samples before a process is demoted at all.</summary>
    public const int HotTicksBeforeThrottle = 2;

    /// <summary>Hot samples at which the demotion escalates. Moderate is the ceiling.</summary>
    public const int HotTicksToModerate = 4;

    /// <summary>Consecutive calm samples a demoted process must show before release.</summary>
    public const int QuietTicksBeforeRelease = 5;

    /// <summary>Calm samples after which the escalation ladder restarts from Light.</summary>
    public const int QuietTicksToForgetHeat = 2;

    /// <summary>
    /// A demoted process is held for at least this long, however calm it looks.
    /// Without it, a single quiet sample between two bursts would restore and
    /// re-demote back to back.
    /// </summary>
    public const double MinimumDwellSeconds = 15.0;

    /// <summary>
    /// One process's CPU usage over a sample window, as a percentage of the whole
    /// machine. <paramref name="deltaTicks"/> is kernel+user time in 100 ns units.
    /// </summary>
    public static double PercentOfTotalCpu(long deltaTicks, double elapsedMs, int cpuCount)
    {
        if (deltaTicks <= 0 || elapsedMs <= 0) return 0;
        return deltaTicks / 10_000.0 / elapsedMs * 100.0 / Math.Max(1, cpuCount);
    }

    /// <summary>Whether a usage figure is high enough to act on.</summary>
    public static bool IsContended(double percentOfTotalCpu)
        => percentOfTotalCpu >= ContentionPercentOfTotalCpu;

    /// <summary>
    /// Turns two CPU-time snapshots into one tick. Pure, so the sampling rules can
    /// be tested without touching the machine.
    ///
    /// Crucially it ALWAYS returns a sample, even an empty one. The session's
    /// release pass lives in the tick handler, and the previous monitor only raised
    /// the event when something was contended - so once every throttled process
    /// went quiet, no tick arrived, the release pass never ran, and those processes
    /// stayed demoted for the rest of the session. An empty hot set is a valid,
    /// expected tick: it is exactly the "everything is calm now" signal.
    /// </summary>
    /// <param name="previous">PID to kernel+user ticks from the last tick.</param>
    /// <param name="current">PID to kernel+user ticks from this tick.</param>
    /// <param name="elapsedMs">Wall time between the two snapshots.</param>
    /// <param name="cpuCount">Logical processors, for the machine-wide share.</param>
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
            // PIDs 0-4 are Idle/System/Secure System and friends: never throttled.
            if (kvp.Key <= 4) continue;

            // No prior reading means this process started inside the window, so its
            // lifetime CPU time would be counted as this tick's usage - a fresh
            // process would look like it had been burning a core since boot.
            if (!previous.TryGetValue(kvp.Key, out long before)) continue;

            double percent = PercentOfTotalCpu(kvp.Value - before, elapsedMs, cpuCount);
            if (IsContended(percent)) hot[kvp.Key] = percent;
        }

        return new ContentionSample(hot, alive);
    }

    /// <summary>Whether a process has been hot long enough to be demoted.</summary>
    public static bool ShouldThrottle(int hotSamples)
        => hotSamples >= HotTicksBeforeThrottle;

    /// <summary>The demotion level for a process that has been hot this many samples.</summary>
    public static AggressivenessLevel EscalationFor(int hotSamples)
        => hotSamples >= HotTicksToModerate ? AggressivenessLevel.Moderate : AggressivenessLevel.Light;

    /// <summary>
    /// Whether a demoted process may be restored now. Both conditions matter:
    /// calm enough that it is not about to burst again, and held long enough that
    /// restoring cannot immediately undo itself.
    /// </summary>
    public static bool ShouldRelease(int quietTicks, double managedSeconds)
        => quietTicks >= QuietTicksBeforeRelease && managedSeconds >= MinimumDwellSeconds;

    /// <summary>Whether a calm stretch has lasted long enough to forget prior heat.</summary>
    public static bool ShouldForgetHeat(int quietTicks)
        => quietTicks >= QuietTicksToForgetHeat;
}
