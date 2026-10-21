using System;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public enum TelemetryHealth
    {
        Available,

        Unavailable,
    }

    public sealed record TelemetryUpdate(GpuTelemetrySnapshot? Snapshot, TelemetryHealth Health, string? ErrorDetail);

    public sealed class GpuTelemetryPollingService : IDisposable
    {
        private readonly IGpuTuningController _controller;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _inFlight;
        private TimeSpan _interval;

        public GpuTelemetryPollingService(IGpuTuningController controller, TimeSpan? interval = null)
        {
            _controller = controller;
            _interval = interval ?? TimeSpan.FromMilliseconds(1000);
        }

        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        public event Action<TelemetryUpdate>? Update;

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
                        Update?.Invoke(new TelemetryUpdate(null, TelemetryHealth.Unavailable, ex.GetType().Name));
                    }
                    finally
                    {
                        Volatile.Write(ref _inFlight, 0);
                    }
                }

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
