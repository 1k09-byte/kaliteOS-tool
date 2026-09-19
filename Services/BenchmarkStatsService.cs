using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Pure frametime statistics (CapFrameX conventions, clearly labeled).
/// FPS = 1000 / frametimeMs per frame. No UI, no IO — unit-tested.
/// </summary>
public static class BenchmarkStatsService
{
    private const double StutterFactor = 2.5;
    private const double StutterWindowMs = 1000.0;
    private const double LowFpsThresholdMs = 40.0;

    /// <summary>
    /// Drops warmup/cooldown edges by time: keeps frames with
    /// t0+headMs &lt;= t &lt;= tLast-tailMs. PresentMon attach, countdown tail,
    /// alt-tab-to-stop and minimize gaps all land in the edges — a single
    /// 900 ms stall there otherwise defines the entire time-weighted 1% low
    /// (e.g. 1.1 FPS on a 1900 FPS run). Falls back to untrimmed when stamps
    /// are missing/mismatched or the window would empty the run.
    /// </summary>
    public static List<float> WindowByTime(
        IReadOnlyList<float> frames,
        IReadOnlyList<double>? stamps,
        double headMs = 2000,
        double tailMs = 1000)
    {
        var all = frames.ToList();
        if (stamps == null || stamps.Count != frames.Count || frames.Count == 0)
            return all;
        double t0 = stamps[0];
        double t1 = stamps[^1];
        var kept = new List<float>();
        for (int i = 0; i < frames.Count; i++)
        {
            double t = stamps[i] - t0;
            if (t >= headMs && t <= (t1 - t0) - tailMs) kept.Add(frames[i]);
        }
        return kept.Count > 0 ? kept : all;
    }

    public static BenchmarkStats Compute(
        IReadOnlyList<float> frametimesMs,
        double trimStartFraction = 0.05,
        double trimEndFraction = 0.0,
        bool dropOutliers = false)
    {
        var empty = new BenchmarkStats();
        if (frametimesMs == null || frametimesMs.Count == 0) return empty;

        // Windowing: trim head warmup / tail by frame count.
        int n0 = frametimesMs.Count;
        int cutStart = (int)Math.Floor(n0 * Math.Clamp(trimStartFraction, 0, 0.5));
        int cutEnd = (int)Math.Floor(n0 * Math.Clamp(trimEndFraction, 0, 0.5));
        cutStart = Math.Min(cutStart, n0 - 1);
        cutEnd = Math.Min(cutEnd, n0 - 1 - cutStart);
        var frames = frametimesMs.Skip(cutStart).Take(n0 - cutStart - cutEnd)
            .Where(f => f > 0 && !float.IsNaN(f) && !float.IsInfinity(f)).ToList();
        if (frames.Count == 0) return empty;

        // Outlier toggle: drop worst ceil(n*0.1%) frames (spike-immune percentiles).
        if (dropOutliers && frames.Count >= 10)
        {
            int drop = Math.Max(1, (int)Math.Ceiling(frames.Count * 0.001));
            frames = frames.OrderBy(f => f).Take(frames.Count - drop).ToList();
            if (frames.Count == 0) return empty;
        }

        var fps = frames.Select(f => 1000.0 / f).ToList();
        double totalMs = frames.Sum(f => (double)f);
        int n = frames.Count;

        var ascFps = fps.OrderBy(v => v).ToList(); // worst first
        var ascMs = frames.OrderBy(v => v).ToList();

        double Percentile(IReadOnlyList<double> sorted, double p01)
        {
            if (sorted.Count == 1) return sorted[0];
            double rank = p01 / 100.0 * (sorted.Count - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }

        double Median(IReadOnlyList<double> sorted)
        {
            int m = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2.0;
        }

        // 1% Low (count): mean FPS of worst ceil(n*1%) frames.
        int k1 = Math.Max(1, (int)Math.Ceiling(n * 0.01));
        double low1Count = ascFps.Take(k1).Average();

        // 0.1% Low (count): mean FPS of worst ceil(n*0.1%) frames.
        // Count-averaged lows dilute isolated giant stalls (a single 900 ms
        // hitch among 100k frames barely moves them), unlike time-weighted
        // lows where one stall can define the whole number. This matches
        // PresentMon-viewer "X% Low Avg" semantics.
        int k01 = Math.Max(1, (int)Math.Ceiling(n * 0.001));
        double low01Count = ascFps.Take(k01).Average();

        // Time-weighted lows: worst frametimes covering x% of total time;
        // result = FPS of the frame that crosses the budget (CapFrameX style).
        double TimeWeightedLow(double fraction)
        {
            var desc = frames.OrderByDescending(f => f).ToList();
            double budget = totalMs * fraction;
            double acc = 0;
            foreach (float f in desc)
            {
                acc += f;
                if (acc >= budget) return 1000.0 / f;
            }
            return ascFps[0];
        }

        // Stutter vs rolling median (~1s trailing window by time).
        var events = new List<StutterEvent>();
        double t = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                double wAcc = 0;
                var window = new List<double>();
                for (int j = i - 1; j >= 0; j--)
                {
                    window.Add(frames[j]);
                    wAcc += frames[j];
                    if (wAcc >= StutterWindowMs) break;
                }
                window.Sort();
                double med = Median(window);
                if (med > 0 && frames[i] > StutterFactor * med)
                {
                    events.Add(new StutterEvent
                    {
                        TimestampMs = t,
                        DurationMs = frames[i],
                        Severity = frames[i] / med,
                    });
                }
            }
            t += frames[i];
        }

        return new BenchmarkStats
        {
            FrameCount = n,
            TotalTimeMs = totalMs,
            AverageFps = n / (totalMs / 1000.0),
            // Harmonic mean of per-frame FPS == frames/totalTime (same series).
            HarmonicAvgFps = n / (totalMs / 1000.0),
            ArithmeticAvgFps = fps.Average(),
            MedianFps = Median(ascFps),
            MinFps = ascFps[0],
            MaxFps = ascFps[n - 1],
            P1Fps = Percentile(ascFps, 1.0),
            P02Fps = Percentile(ascFps, 0.2),
            Low1CountFps = low1Count,
            Low01CountFps = low01Count,
            Low1TimeFps = TimeWeightedLow(0.01),
            Low01TimeFps = TimeWeightedLow(0.001),
            StutterCount = events.Count,
            StutterPct = 100.0 * events.Count / n,
            LowFpsTimeMs = frames.Where(f => f > LowFpsThresholdMs).Sum(f => (double)f),
            StutterEvents = events,
        };
    }

    /// <summary>Linear-interpolation percentile over an ascending-sorted list.</summary>
    public static double PercentileOf(IReadOnlyList<double> sortedAsc, double p01)
    {
        if (sortedAsc == null || sortedAsc.Count == 0) return double.NaN;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        double rank = Math.Clamp(p01, 0, 100) / 100.0 * (sortedAsc.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return lo == hi ? sortedAsc[lo] : sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }

    /// <summary>
    /// Frametime distribution in ms (ascending percentiles + worst-averages),
    /// mirroring the viewer panel: P1, Average, P95, P99, 1% High Average,
    /// P99.8, P99.9, 0.1% High Average.
    /// </summary>
    public sealed class FrametimeSummary
    {
        public double P1, Avg, P95, P99, HighAvg1, P998, P999, HighAvg01;
        public int Frames;
    }

    public static FrametimeSummary SummarizeFrametimes(IReadOnlyList<float> frames)
    {
        var out_ = new FrametimeSummary();
        if (frames == null || frames.Count == 0) return out_;
        var asc = frames.Select(f => (double)f).OrderBy(v => v).ToList();
        out_.Frames = asc.Count;
        out_.P1 = PercentileOf(asc, 1);
        out_.Avg = asc.Average();
        out_.P95 = PercentileOf(asc, 95);
        out_.P99 = PercentileOf(asc, 99);
        out_.P998 = PercentileOf(asc, 99.8);
        out_.P999 = PercentileOf(asc, 99.9);
        int k1 = Math.Max(1, (int)Math.Ceiling(asc.Count * 0.01));
        int k01 = Math.Max(1, (int)Math.Ceiling(asc.Count * 0.001));
        out_.HighAvg1 = asc.Skip(asc.Count - k1).Average();
        out_.HighAvg01 = asc.Skip(asc.Count - k01).Average();
        return out_;
    }

    /// <summary>
    /// Trailing time-window moving average per frame (500 ms default).
    /// Stamps are relative ms; without usable stamps falls back to an
    /// index window of n/100 frames. Pure.
    /// </summary>
    public static List<double> MovingAverage(
        IReadOnlyList<float> frames, IReadOnlyList<double>? stamps, double windowMs = 500)
    {
        var out_ = new List<double>();
        if (frames == null || frames.Count == 0) return out_;
        bool timed = stamps != null && stamps.Count == frames.Count && frames.Count > 1;
        double t0 = timed ? stamps[0] : 0;
        int start = 0;
        double acc = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            acc += frames[i];
            if (timed)
            {
                double t = stamps[i] - t0;
                while (start < i && (t - (stamps[start] - t0)) > windowMs)
                {
                    acc -= frames[start];
                    start++;
                }
            }
            else
            {
                int win = Math.Max(1, frames.Count / 100);
                while (i - start + 1 > win)
                {
                    acc -= frames[start];
                    start++;
                }
            }
            out_.Add(acc / (i - start + 1));
        }
        return out_;
    }

    /// <summary>
    /// Exclusive time breakdown: stutter frames (&gt;2.5x trailing ~1 s
    /// median) vs low-FPS-only frames (&gt;40 ms, not stutter) vs smooth rest.
    /// </summary>
    public sealed class TimeBreakdown
    {
        public double SmoothMs, LowOnlyMs, StutterMs, TotalMs;
    }

    public static TimeBreakdown Breakdown(IReadOnlyList<float> frames)
    {
        var out_ = new TimeBreakdown();
        if (frames == null || frames.Count == 0) return out_;
        var window = new List<double>();
        double wAcc = 0;
        foreach (float f in frames)
        {
            out_.TotalMs += f;
            // trailing ~1 s median over prior frames
            var sorted = window.OrderBy(v => v).ToList();
            double med = sorted.Count == 0 ? f :
                (sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                 : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0);
            bool stutter = sorted.Count > 0 && med > 0 && f > StutterFactor * med;
            if (stutter) out_.StutterMs += f;
            else if (f > LowFpsThresholdMs) out_.LowOnlyMs += f;
            else out_.SmoothMs += f;
            window.Add(f);
            wAcc += f;
            while (window.Count > 0 && wAcc - window[0] > StutterWindowMs)
            {
                wAcc -= window[0];
                window.RemoveAt(0);
            }
        }
        return out_;
    }

    /// <summary>Consecutive absolute frametime deltas binned in ms bands.</summary>
    public static (int Under1, int Under5, int Under10, int Over10) VarianceBands(
        IReadOnlyList<float> frames)
    {
        int a = 0, b = 0, c = 0, d = 0;
        if (frames != null)
        {
            for (int i = 1; i < frames.Count; i++)
            {
                double delta = Math.Abs(frames[i] - frames[i - 1]);
                if (delta < 1) a++;
                else if (delta < 5) b++;
                else if (delta < 10) c++;
                else d++;
            }
        }
        return (a, b, c, d);
    }

    /// <summary>Frames below each FPS threshold (count + share).</summary>
    public static List<(int Fps, int Count, double Share)> ThresholdCounts(
        IReadOnlyList<float> frames, int[]? thresholds = null)
    {
        thresholds ??= new[] { 30, 60, 120, 240 };
        var out_ = new List<(int, int, double)>();
        if (frames == null || frames.Count == 0) return out_;
        foreach (int t in thresholds)
        {
            int count = frames.Count(f => f > 0 && 1000.0 / f < t);
            out_.Add((t, count, (double)count / frames.Count));
        }
        return out_;
    }
}
