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
    ///
    /// Crash-orphan protection (phase-2 finding): a HARD process kill cannot
    /// run Dispose, which would leave the fan forced forever. While the loop
    /// runs it maintains a liveness marker file (pid + current %); the next
    /// app start calls EnsureNoOrphanedFanControl, which detects a marker
    /// whose pid is gone (or stale) and restores driver fan control before
    /// the user can ever see a stuck fan.
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
        private readonly string _markerPath;

        /// <summary>Raised when the loop stops for any reason with the reason text.</summary>
        public event Action<string?>? Stopped;

        public FanCurveExecutionService(INvidiaGpuController controller, string? markerDirectory = null)
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

        /// <summary>
        /// Startup recovery: if a previous process died mid-Curve-mode, the
        /// marker it left behind proves a forced fan may still be active.
        /// Restores driver control (a real Auto write) and clears the marker.
        /// Returns true when an orphaned state was found and recovered.
        /// </summary>
        public static bool EnsureNoOrphanedFanControl(INvidiaGpuController controller, string? directory = null)
        {
            var path = GetMarkerPath(directory);
            try
            {
                if (!File.Exists(path)) return false;

                // Marker format: pid|percent|timestampUtc (the loop rewrites it
                // every tick, so a live loop's marker is always fresh).
                var parts = File.ReadAllText(path).Split('|');
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid);
                DateTime.TryParse(parts.Length > 2 ? parts[2] : null, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var stamp);
                var fresh = stamp != default && DateTime.UtcNow - stamp < TimeSpan.FromMinutes(1);

                // Our OWN pid with a fresh marker = a live curve loop in this very
                // process owns the fan (double init, page re-navigation) — never
                // fight it. A stale own-pid marker is pid-reuse debris → recover.
                if (pid > 0 && pid == Environment.ProcessId && fresh) return false;

                // Another live instance owns the fan — do not fight it.
                if (pid > 0 && pid != Environment.ProcessId)
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (!proc.HasExited) return false;
                    }
                    catch (System.ArgumentException) { /* pid gone — orphan confirmed */ }
                }

                controller.RestoreFanAuto(); // explicit hand-back, never "just delete the file"
                File.Delete(path);
                return true;
            }
            catch
            {
                // Recovery must never block startup; worst case the marker
                // stays and the next start retries.
                return false;
            }
        }

        public TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        private int _maxStepPercentPerTick;

        /// <summary>
        /// Ramp/smoothing cap: maximum percentage-points the applied fan speed
        /// may move toward the curve's target per tick. 0 (default) disables
        /// smoothing — the target applies directly, exactly as before. Damps
        /// audible fan-speed hunting when the temperature hovers near a curve
        /// knee. Read live every tick, like <see cref="Interval"/>.
        /// </summary>
        public int MaxStepPercentPerTick
        {
            get { lock (_gate) return _maxStepPercentPerTick; }
            set { lock (_gate) _maxStepPercentPerTick = Math.Max(0, value); }
        }

        private int? _lastAppliedPct;

        /// <summary>
        /// Pure ramp step: moves <paramref name="last"/> toward
        /// <paramref name="target"/> by at most <paramref name="maxStep"/>
        /// points. maxStep &lt;= 0 means no smoothing (target directly).
        /// </summary>
        public static int RampTowards(int last, int target, int maxStep)
        {
            if (maxStep <= 0) return target;
            return last + Math.Clamp(target - last, -maxStep, maxStep);
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
                WriteMarkerSafe(_points[^1].FanPercent);
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
                lock (_gate) _lastAppliedPct = null;
                ClearMarkerSafe();
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
                    // Quiesced (device restarts in flight): hold the last
                    // speed and skip the write — same native-handle hazard
                    // as telemetry (see HardwareQuiesceGate).
                    if (!HardwareQuiesceGate.IsQuiesced)
                    {
                        WriteMarkerSafe(pct); // liveness: pid + current forced % for crash recovery
                        // GPU disconnected mid-session makes writes fail; the write
                        // result is reported through Stopped and the loop exits.
                        var res = await Task.Run(() => _controller.SetFanStaticPercent(pct), token).ConfigureAwait(false);
                        if (res.IsSuccess)
                        {
                            lock (_gate) _lastAppliedPct = pct;
                        }
                        else if (res.ErrorKind == OverclockErrorKind.GpuDisconnected)
                        {
                            await StopAsync("GPU disconnected — fan restored to auto").ConfigureAwait(false);
                            return;
                        }
                    }
                }
                // temp == null: telemetry unavailable this tick — hold last speed
                // rather than slamming 100% or 0% on a single missed read.

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
            catch { /* marker is best-effort; control path is unaffected */ }
        }

        private void ClearMarkerSafe()
        {
            try { if (File.Exists(_markerPath)) File.Delete(_markerPath); }
            catch { }
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
