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
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.ProcessOptimizer.Services;

public interface IContentionMonitor
{
    event EventHandler<ContentionSample>? ContentionDetected;
    void StartMonitoring();
    void StopMonitoring();
}

public sealed class ContentionMonitor : IContentionMonitor, IDisposable
{
    private const int TickMilliseconds = 1000;

    private const double MinimumSampleWindowMs = 250;

    private readonly object _lock = new();
    private readonly Dictionary<int, long> _previousTicks = new();
    private CancellationTokenSource? _cts;
    private DateTime _lastPoll = DateTime.UtcNow;
    private bool _isRunning;

    public event EventHandler<ContentionSample>? ContentionDetected;

    public void StartMonitoring()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _previousTicks.Clear();
            _lastPoll = DateTime.UtcNow;

            Task.Run(() => MonitorLoopAsync(_cts.Token));
        }
    }

    public void StopMonitoring()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            cts = _cts;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickMilliseconds, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested) break;

            try
            {
                Sample(token);
            }
            catch
            {
            }
        }
    }

    private void Sample(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        double elapsedMs = (now - _lastPoll).TotalMilliseconds;
        _lastPoll = now;
        if (elapsedMs < MinimumSampleWindowMs) return;

        if (!SystemProcessCpuReader.TryRead(out Dictionary<int, long> current)) return;

        ContentionSample sample = ContentionPolicy.ComputeTick(
            _previousTicks, current, elapsedMs, Math.Max(1, Environment.ProcessorCount));

        _previousTicks.Clear();
        foreach (var kvp in current)
        {
            _previousTicks[kvp.Key] = kvp.Value;
        }

        if (token.IsCancellationRequested) return;

        ContentionDetected?.Invoke(this, sample);
    }

    public void Dispose() => StopMonitoring();
}
