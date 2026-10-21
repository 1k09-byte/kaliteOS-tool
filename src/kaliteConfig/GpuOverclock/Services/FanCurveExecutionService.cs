using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class FanCurveExecutionService : IAsyncDisposable
    {
        private readonly IGpuTuningController _controller;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private FanCurvePoint[] _points = Array.Empty<FanCurvePoint>();
        private Func<int?>? _tempProvider;
        private TimeSpan _interval = TimeSpan.FromSeconds(2);
        private readonly string _markerPath;

        public event Action<string?>? Stopped;

        public FanCurveExecutionService(IGpuTuningController controller, string? markerDirectory = null)
        {
            _controller = controller;
            _markerPath = GetMarkerPath(markerDirectory);
        }

        internal static string GetMarkerPath(string? directory)
        {
            var dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig");
            return Path.Combine(dir, "gpu-overclock-fan-curve.active");
        }

        public static bool EnsureNoOrphanedFanControl(IGpuTuningController controller, string? directory = null)
        {
            var path = GetMarkerPath(directory);
            try
            {
                if (!File.Exists(path)) return false;

                var parts = File.ReadAllText(path).Split('|');
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid);
                DateTime.TryParse(parts.Length > 2 ? parts[2] : null, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var stamp);
                var fresh = stamp != default && DateTime.UtcNow - stamp < TimeSpan.FromMinutes(1);

                if (pid > 0 && pid == Environment.ProcessId && fresh) return false;

                if (pid > 0 && pid != Environment.ProcessId)
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (!proc.HasExited) return false;
                    }
                    catch (System.ArgumentException) {  }
                }

                controller.RestoreFanAuto();
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        private int _maxStepPercentPerTick;

        public int MaxStepPercentPerTick
        {
            get { lock (_gate) return _maxStepPercentPerTick; }
            set { lock (_gate) _maxStepPercentPerTick = Math.Max(0, value); }
        }

        private int? _lastAppliedPct;

        public static int RampTowards(int last, int target, int maxStep)
        {
            if (maxStep <= 0) return target;
            return last + Math.Clamp(target - last, -maxStep, maxStep);
        }

        public void Start(FanCurvePoint[] points, Func<int?> tempProvider)
        {
            if (points is null || points.Length == 0) return;
            lock (_gate)
            {
                _points = points.OrderBy(p => p.TempC).ToArray();
                _tempProvider = tempProvider;
                if (_loop != null && !_loop.IsCompleted) return;
                WriteMarkerSafe(_points[^1].FanPercent);
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => LoopAsync(token), token);
            }
        }

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
                lock (_gate) _lastAppliedPct = null;
                ClearMarkerSafe();
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
                int maxStep;
                int? lastApplied;
                lock (_gate)
                {
                    interval = _interval;
                    maxStep = _maxStepPercentPerTick;
                    lastApplied = _lastAppliedPct;
                    temp = _tempProvider?.Invoke();
                }

                if (temp.HasValue)
                {
                    var target = Evaluate(_points, temp.Value);
                    var pct = lastApplied.HasValue ? RampTowards(lastApplied.Value, target, maxStep) : target;
                    if (!HardwareQuiesceGate.IsQuiesced)
                    {
                        WriteMarkerSafe(pct);
                        var res = await Task.Run(() => _controller.SetFanStaticPercent(pct), token).ConfigureAwait(false);
                        if (res.IsSuccess)
                        {
                            lock (_gate) _lastAppliedPct = pct;
                        }
                        else if (res.ErrorKind == OverclockErrorKind.GpuDisconnected)
                        {
                            await StopAsync("GPU disconnected - fan restored to auto").ConfigureAwait(false);
                            return;
                        }
                    }
                }

                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void WriteMarkerSafe(int percent)
        {
            try
            {
                var dir = Path.GetDirectoryName(_markerPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(_markerPath,
                    string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:O}", Environment.ProcessId, percent, DateTime.UtcNow));
            }
            catch {  }
        }

        private void ClearMarkerSafe()
        {
            try { if (File.Exists(_markerPath)) File.Delete(_markerPath); }
            catch { }
        }

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
            await StopAsync("app closing - fan restored to auto").ConfigureAwait(false);
        }
    }
}
