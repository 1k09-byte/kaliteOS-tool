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

namespace kaliteConfig.Models;

/// <summary>Hardware/software conditions a run was captured under (A/B captions).</summary>
public sealed class BenchmarkSystemInfo
{
    public string CpuName { get; set; } = string.Empty;
    public string GpuName { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string WindowsBuild { get; set; } = string.Empty;
    public string PowerPlan { get; set; } = string.Empty;
    public bool GamingMode { get; set; }
    public string CpuSetsPartition { get; set; } = string.Empty;
}

/// <summary>One sensor sample joined to frames by timestamp.</summary>
public sealed class SensorSample
{
    public DateTime Timestamp { get; set; }
    public float? GpuTempC { get; set; }
    public float? GpuPowerW { get; set; }
    public float? GpuUtilPct { get; set; }
    public float? CpuPct { get; set; }
    public bool ThrottleFlag { get; set; }
}

/// <summary>A single stutter event (frametime &gt; 2.5x rolling median).</summary>
public sealed class StutterEvent
{
    public double TimestampMs { get; set; }
    public double DurationMs { get; set; }
    public double Severity { get; set; } // duration / rolling median
}

/// <summary>A captured benchmark run. Frametimes in milliseconds.</summary>
public sealed class BenchmarkRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Game { get; set; } = string.Empty;
    public string Exe { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Now;
    public double DurationSec { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4CC2FF";
    public BenchmarkSystemInfo SystemInfo { get; set; } = new();
    public float[] FrametimesMs { get; set; } = Array.Empty<float>();
    public double[] TimestampsMs { get; set; } = Array.Empty<double>();
    public List<SensorSample> Sensors { get; set; } = new();
}

/// <summary>Computed statistics for a run (all FPS unless named ms).</summary>
public sealed class BenchmarkStats
{
    public int FrameCount { get; set; }
    public double TotalTimeMs { get; set; }
    public double AverageFps { get; set; }
    public double ArithmeticAvgFps { get; set; }
    public double HarmonicAvgFps { get; set; }
    public double MedianFps { get; set; }
    public double MinFps { get; set; }
    public double MaxFps { get; set; }
    public double P1Fps { get; set; }
    public double P02Fps { get; set; }
    public double Low1CountFps { get; set; }
    public double Low01CountFps { get; set; }
    public double Low1TimeFps { get; set; }
    public double Low01TimeFps { get; set; }
    public int StutterCount { get; set; }
    public double StutterPct { get; set; }
    public double LowFpsTimeMs { get; set; }
    public List<StutterEvent> StutterEvents { get; set; } = new();
}
