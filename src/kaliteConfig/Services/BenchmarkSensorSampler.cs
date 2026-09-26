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
using System.Diagnostics;
using System.Linq;
using kaliteConfig.GpuOverclock.Services;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Samples GPU telemetry + target-process CPU% during a capture only.
/// Uses the shared <see cref="GpuTelemetryPollingService"/> at 500 ms and
/// restores its previous interval on Stop (never stops a loop it didn't start).
/// CPU% comes from process TotalProcessorTime deltas (no UI rows needed).
/// Throttle mark: core temp &gt;= 83C, hotspot &gt;= 95C, or power &gt;= 98% of limit.
/// </summary>
public sealed class BenchmarkSensorSampler
{
    private const double TempThrottleC = 83.0;
    private const double HotspotThrottleC = 95.0;
    private const double PowerLimitThrottlePct = 98.0;
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(500);

    private readonly List<SensorSample> _samples = new();
    private readonly object _gate = new();
    private readonly GpuTelemetryPollingService _telemetry =
        GpuOverclock.GpuOverclockModule.Instance.Telemetry;

    private TimeSpan _priorInterval = TimeSpan.FromSeconds(1);
    private int _pid;
    private TimeSpan _lastProcTime;
    private DateTime _lastWallUtc;
    private bool _running;

    public DateTime CaptureStartUtc { get; private set; }

    public void Start(int targetPid)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _pid = targetPid;
            _samples.Clear();
            CaptureStartUtc = DateTime.UtcNow;
            _lastProcTime = ReadProcTime(targetPid);
            _lastWallUtc = DateTime.UtcNow;
            _priorInterval = _telemetry.Interval;
            _telemetry.Interval = CaptureInterval;
            _telemetry.Update += OnTelemetry;
            _telemetry.Start();
        }
    }

    public List<SensorSample> Stop()
    {
        lock (_gate)
        {
            if (!_running) return new List<SensorSample>(_samples);
            _running = false;
            _telemetry.Update -= OnTelemetry;
            try { _telemetry.Interval = _priorInterval; } catch { }
            return new List<SensorSample>(_samples);
        }
    }

    private void OnTelemetry(TelemetryUpdate update)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan proc = ReadProcTime(_pid);
            double wallMs = Math.Max(1, (now - _lastWallUtc).TotalMilliseconds);
            float? cpu = null;
            if (_lastProcTime >= TimeSpan.Zero && proc >= TimeSpan.Zero)
            {
                cpu = (float)Math.Clamp(
                    (proc - _lastProcTime).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0,
                    0, 100 * Environment.ProcessorCount);
            }
            _lastProcTime = proc;
            _lastWallUtc = now;

            var snap = update.Snapshot;
            bool throttle = (snap?.GpuTempC >= TempThrottleC)
                || (snap?.HotspotTempC >= HotspotThrottleC)
                || (snap?.PowerDrawPercentOfLimit >= PowerLimitThrottlePct);

            lock (_gate)
            {
                if (!_running) return;
                _samples.Add(new SensorSample
                {
                    Timestamp = now,
                    GpuTempC = snap?.GpuTempC,
                    GpuPowerW = (float?)snap?.PowerDrawW,
                    GpuUtilPct = snap?.GpuUsagePercent,
                    CpuPct = cpu,
                    ThrottleFlag = throttle,
                });
            }
        }
        catch { }
    }

    private static TimeSpan ReadProcTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.TotalProcessorTime;
        }
        catch { return TimeSpan.FromTicks(-1); }
    }
}
