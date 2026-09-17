using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Curve-mode fan loop: reads temp from the latest telemetry snapshot,
    /// linearly interpolates the user's points, and applies the resulting %
    /// through the same static-speed path Static mode uses.
    ///
    /// The loop stops and hands back to Auto (a real restore call) when:
    /// the app is closing, the GPU is disconnected/re-detected, or the user
    /// switches away from Curve mode. "Silently keeps running after the UI
    /// closes" is a bug class this class is explicitly designed against —
    /// Dispose always restores Auto.
    /// </summary>
    public sealed class FanCurveExecutionService : IAsyncDisposable
    {
        private readonly INvidiaGpuController _controller;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private FanCurvePoint[] _points = Array.Empty<FanCurvePoint>();
        private Func<int?>? _tempProvider;
        private TimeSpan _interval = TimeSpan.FromSeconds(2);

        /// <summary>Raised when the loop stops for any reason with the reason text.</summary>
        public event Action<string?>? Stopped;

        public FanCurveExecutionService(INvidiaGpuController controller)
        {
            _controller = controller;
        }

        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        /// <summary>
        /// Starts (or restarts with new points) the curve loop. The temp provider
        /// is usually the polling service's latest snapshot — kept as a delegate
        /// so this service never depends on the polling implementation.
        /// </summary>
        public void Start(FanCurvePoint[] points, Func<int?> tempProvider)
        {
            if (points is null || points.Length == 0) return;
            lock (_gate)
            {
                _points = points.OrderBy(p => p.TempC).ToArray();
                _tempProvider = tempProvider;
                if (_loop != null && !_loop.IsCompleted) return; // already running; points updated
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(token), token);
            }
        }

        /// <summary>Stops the loop and restores driver fan control (never silently persists).</summary>
        public async Task StopAsync(string? reason)
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate)
            {
                cts = _cts;
                loop = _loop;
                _cts = null;
                _loop = null;
                _tempProvider = null;
            }
            if (cts != null)
            {
                cts.Cancel();
                if (loop != null)
                {
                    try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                cts.Dispose();
                // Explicit hand-back to the driver default — not "just stop writing".
                await Task.Run(() => _controller.RestoreFanAuto()).ConfigureAwait(false);
                Stopped?.Invoke(reason);
            }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                int? temp = null;
                TimeSpan interval;
                lock (_gate)
                {
                    interval = _interval;
                    temp = _tempProvider?.Invoke();
                }

                if (temp.HasValue)
                {
                    var pct = Evaluate(_points, temp.Value);
                    // GPU disconnected mid-session makes writes fail; the write
                    // result is reported through Stopped and the loop exits.
                    var res = await Task.Run(() => _controller.SetFanStaticPercent(pct), token).ConfigureAwait(false);
                    if (!res.IsSuccess && res.ErrorKind == OverclockErrorKind.GpuDisconnected)
                    {
                        await StopAsync("GPU disconnected — fan restored to auto").ConfigureAwait(false);
                        return;
                    }
                }
                // temp == null: telemetry unavailable this tick — hold last speed
                // rather than slamming 100% or 0% on a single missed read.

                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Pure interpolation: piecewise-linear between sorted points, clamped
        /// below the first and above the last. Percent floors at 0.
        /// </summary>
        public static int Evaluate(FanCurvePoint[] sortedPoints, int tempC)
        {
            if (sortedPoints is null || sortedPoints.Length == 0) return 0;
            var pts = sortedPoints.OrderBy(p => p.TempC).ToArray();
            if (tempC <= pts[0].TempC) return Math.Max(0, pts[0].FanPercent);
            if (tempC >= pts[^1].TempC) return Math.Max(0, pts[^1].FanPercent);
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                if (tempC >= a.TempC && tempC <= b.TempC)
                {
                    if (b.TempC == a.TempC) return Math.Max(0, b.FanPercent);
                    var t = (double)(tempC - a.TempC) / (b.TempC - a.TempC);
                    return Math.Max(0, (int)Math.Round(a.FanPercent + t * (b.FanPercent - a.FanPercent)));
                }
            }
            return Math.Max(0, pts[^1].FanPercent);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync("app closing — fan restored to auto").ConfigureAwait(false);
        }
    }
}
