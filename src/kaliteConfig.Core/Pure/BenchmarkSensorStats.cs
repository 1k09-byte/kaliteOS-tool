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
using kaliteConfig.Models;

namespace kaliteConfig.Services;

public sealed class SensorChannelSummary
{
    public bool HasData { get; set; }
    public double Min { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
}

public sealed class SensorSummary
{
    public SensorChannelSummary GpuTempC { get; set; } = new();
    public SensorChannelSummary GpuPowerW { get; set; } = new();
    public SensorChannelSummary GpuUtilPct { get; set; } = new();
    public SensorChannelSummary CpuPct { get; set; } = new();
    public int ThrottleMarks { get; set; }
    public List<double> ThrottleFracs { get; set; } = new();
}

public static class BenchmarkSensorStats
{
    public static SensorSummary Summarize(IReadOnlyList<SensorSample> samples)
    {
        var out_ = new SensorSummary();
        if (samples == null || samples.Count == 0) return out_;
        out_.GpuTempC = Channel(samples.Select(s => (double?)s.GpuTempC));
        out_.GpuPowerW = Channel(samples.Select(s => (double?)s.GpuPowerW));
        out_.GpuUtilPct = Channel(samples.Select(s => (double?)s.GpuUtilPct));
        out_.CpuPct = Channel(samples.Select(s => (double?)s.CpuPct));
        double spanMs = Math.Max(1, (samples[^1].Timestamp - samples[0].Timestamp).TotalMilliseconds);
        for (int i = 0; i < samples.Count; i++)
            if (samples[i].ThrottleFlag)
            {
                out_.ThrottleMarks++;
                out_.ThrottleFracs.Add(
                    (samples[i].Timestamp - samples[0].Timestamp).TotalMilliseconds / spanMs);
            }
        return out_;
    }

    private static SensorChannelSummary Channel(IEnumerable<double?> values)
    {
        var vals = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (vals.Count == 0) return new SensorChannelSummary();
        return new SensorChannelSummary
        {
            HasData = true,
            Min = vals.Min(),
            Avg = vals.Average(),
            Max = vals.Max(),
        };
    }

    public static SensorSample? NearestAt(IReadOnlyList<SensorSample> samples, double frac)
    {
        if (samples == null || samples.Count == 0) return null;
        int i = (int)Math.Round(Math.Clamp(frac, 0, 1) * (samples.Count - 1));
        return samples[i];
    }

    public static List<double> GpuUtilSeries(IReadOnlyList<SensorSample> samples, int bucketCount)
    {
        var out_ = new List<double>();
        if (samples == null || samples.Count == 0 || bucketCount <= 0) return out_;
        for (int b = 0; b < bucketCount; b++)
        {
            var s = NearestAt(samples, bucketCount == 1 ? 0 : (double)b / (bucketCount - 1));
            out_.Add(s?.GpuUtilPct ?? double.NaN);
        }
        return out_;
    }
}
