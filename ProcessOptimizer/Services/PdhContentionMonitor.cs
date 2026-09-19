using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Native;

namespace kaliteConfig.ProcessOptimizer.Services;

public interface IContentionMonitor
{
    event EventHandler<Dictionary<int, double>> ContentionDetected;
    void StartMonitoring();
    void StopMonitoring();
}

public class PdhContentionMonitor : IContentionMonitor, IDisposable
{
    private CancellationTokenSource _cts;
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly Dictionary<int, long> _previousTicks = new();
    private DateTime _lastPoll = DateTime.UtcNow;
    private readonly object _lock = new();
    private bool _isRunning;

    // Report processes using over this % of a single CPU core's capacity to the Orchestrator
    private const double CpuThresholdPercent = 3.0;
    
    public event EventHandler<Dictionary<int, double>> ContentionDetected;

    public void StartMonitoring()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _previousTicks.Clear();
            _lastPoll = DateTime.UtcNow;
            
            Task.Run(MonitorLoopAsync);
        }
    }

    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task MonitorLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(1000, token).ConfigureAwait(false); // 1 second intervals

            if (token.IsCancellationRequested) break;

            var activeContention = new Dictionary<int, double>();
            var now = DateTime.UtcNow;
            double elapsedMs = (now - _lastPoll).TotalMilliseconds;
            _lastPoll = now;

            Process[] procs;
            try { procs = Process.GetProcesses(); } catch { continue; }

            var currentPids = new HashSet<int>();

            foreach (var proc in procs)
            {
                int pid;
                try { pid = proc.Id; } catch { proc.Dispose(); continue; }
                
                if (pid <= 4)
                {
                    proc.Dispose();
                    continue;
                }

                currentPids.Add(pid);

                try
                {
                    using var handle = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);

                    if (!handle.IsInvalid && NativeMethods.Times.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                    {
                        long totalTicks = kernel.ToTicks() + user.ToTicks();

                        if (_previousTicks.TryGetValue(pid, out long prevTotal))
                        {
                            double cpuUsage = ((totalTicks - prevTotal) / 10000.0) / elapsedMs * 100.0 / _processorCount;
                            
                            // If it's using notable continuous CPU, flag it as contention
                            if (cpuUsage > CpuThresholdPercent)
                            {
                                activeContention[pid] = cpuUsage;
                            }
                        }
                        
                        _previousTicks[pid] = totalTicks;
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            // Cleanup dead processes from tracker
            var pidsToRemove = new List<int>();
            foreach (var key in _previousTicks.Keys)
            {
                if (!currentPids.Contains(key)) pidsToRemove.Add(key);
            }
            foreach (var key in pidsToRemove) _previousTicks.Remove(key);

            if (activeContention.Count > 0)
            {
                ContentionDetected?.Invoke(this, activeContention);
            }
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
