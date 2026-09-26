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
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>Current health of the telemetry stream.</summary>
    public enum TelemetryHealth
    {
        /// <summary>Snapshots are flowing normally.</summary>
        Available,

        /// <summary>The last read failed (driver reload, GPU gone). Consumers must
        /// show a distinct "telemetry unavailable" state - never stale values.</summary>
        Unavailable,
    }

    /// <summary>A snapshot plus the stream health at publish time.</summary>
    public sealed record TelemetryUpdate(GpuTelemetrySnapshot? Snapshot, TelemetryHealth Health, string? ErrorDetail);

    /// <summary>
    /// Background polling loop. Guarantees:
    ///  - ticks never overlap: if a read takes longer than the interval, the
    ///    next tick is skipped (no queued piling of NVAPI calls);
    ///  - failures publish a distinct Unavailable update rather than stale or
    ///    zeroed values;
    ///  - events fire on thread-pool threads; subscribers marshal to UI.
    /// </summary>
    public sealed class GpuTelemetryPollingService : IDisposable
    {
        private readonly INvidiaGpuController _controller;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _inFlight; // 0/1 guard against overlapping reads
        private TimeSpan _interval;

        public GpuTelemetryPollingService(INvidiaGpuController controller, TimeSpan? interval = null)
        {
            _controller = controller;
            _interval = interval ?? TimeSpan.FromMilliseconds(1000);
        }

        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        /// <summary>Published every completed tick (success or failure).</summary>
        public event Action<TelemetryUpdate>? Update;

        /// <summary>Starts (or restarts) the loop. Safe to call repeatedly.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null && !_loop.IsCompleted) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(token), token);
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate)
            {
                cts = _cts;
                loop = _loop;
                _cts = null;
                _loop = null;
            }
            if (cts != null)
            {
                cts.Cancel();
                if (loop != null)
                {
                    try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                cts.Dispose();
            }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan interval;
                lock (_gate) interval = _interval;

                if (Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0)
                {
                    try
                    {
                        // Quiesced (device restarts in flight): skip the tick
                        // entirely rather than calling into NVAPI/NVML on
                        // handles a restart may have invalidated - a native
                        // fault there bypasses every managed catch below.
                        if (!HardwareQuiesceGate.IsQuiesced)
                        {
                            var result = await Task.Run(() => _controller.ReadTelemetry(), token).ConfigureAwait(false);
                            if (result.IsSuccess && result.Value != null)
                            {
                                Update?.Invoke(new TelemetryUpdate(result.Value, TelemetryHealth.Available, null));
                            }
                            else
                            {
                                Update?.Invoke(new TelemetryUpdate(null, TelemetryHealth.Unavailable, result.Detail));
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Defensive: the controller converts expected failures to
                        // values, so this is genuinely exceptional - still never crash.
                        Update?.Invoke(new TelemetryUpdate(null, TelemetryHealth.Unavailable, ex.GetType().Name));
                    }
                    finally
                    {
                        Volatile.Write(ref _inFlight, 0);
                    }
                }
                // else: previous read still running - skip this tick.

                var remaining = interval - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try { await Task.Delay(remaining, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }
}
