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

/// <summary>
/// Reports which processes are really competing for the CPU during a session.
///
/// One <c>NtQuerySystemInformation</c> call per tick, diffed against the previous
/// tick: no process handles at all (see <see cref="SystemProcessCpuReader"/>).
/// The previous shape opened a handle to EVERY running process once a second -
/// plus <c>Process.GetProcesses()</c>, which allocates a Process object per PID -
/// for the whole session, which is the opposite of what a game-mode helper should
/// cost.
///
/// It is called ContentionMonitor now. The old name was PdhContentionMonitor and
/// it never touched PDH.
/// </summary>
public sealed class ContentionMonitor : IContentionMonitor, IDisposable
{
    private const int TickMilliseconds = 1000;

    /// <summary>
    /// A sample window shorter than this is not trusted: the percentage is a
    /// delta over elapsed wall time, so a near-zero window inflates it wildly.
    /// </summary>
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
                // One bad sample must never kill the monitor for the session.
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

        // The decision lives in ContentionPolicy.ComputeTick so it can be tested
        // without a machine to measure. This method only supplies the snapshots.
        ContentionSample sample = ContentionPolicy.ComputeTick(
            _previousTicks, current, elapsedMs, Math.Max(1, Environment.ProcessorCount));

        // Rebuild from the current snapshot instead of pruning: the tracker then
        // cannot grow across a session and dead PIDs fall out for free (the old
        // shape needed a separate cleanup pass for exactly that).
        _previousTicks.Clear();
        foreach (var kvp in current)
        {
            _previousTicks[kvp.Key] = kvp.Value;
        }

        if (token.IsCancellationRequested) return;

        // Raised even when NOTHING is contended. The orchestrator's release pass
        // lives in this handler, so suppressing the empty case meant a demoted
        // process could only be restored once some other process got busy - if the
        // session went quiet, it stayed demoted for the rest of the session.
        ContentionDetected?.Invoke(this, sample);
    }

    public void Dispose() => StopMonitoring();
}
