using System;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Process-wide "hands off the GPU hardware" gate.
    ///
    /// WHY THIS EXISTS: restarting a device (pnputil /restart-device on the
    /// GPU itself, which IRQ/Affinity Optimize does) momentarily invalidates
    /// the native handles NVAPI/NVML calls go through. A telemetry or fan
    /// tick landing in that window can fault INSIDE nvapi64.dll/nvml.dll —
    /// a native access violation (0xc0000005) that no managed try/catch can
    /// contain, killing the process with a "System Error" dialog. So every
    /// flow that restarts devices holds this gate across the restarts plus a
    /// PnP settle delay, and every background GPU loop skips its tick while
    /// held. A gap in the graphs is always preferable to a dead process.
    ///
    /// Ref-counted and thread-safe; nested holds are fine. Keep holds short
    /// (seconds, not minutes) — telemetry simply pauses while held.
    /// </summary>
    public static class HardwareQuiesceGate
    {
        private static readonly object _gate = new();
        private static int _holders;
        private static string? _reason;

        /// <summary>True while at least one holder is active.</summary>
        public static bool IsQuiesced
        {
            get { lock (_gate) return _holders > 0; }
        }

        /// <summary>Why the hardware is currently held (newest holder wins), or null.</summary>
        public static string? Reason
        {
            get { lock (_gate) return _reason; }
        }

        /// <summary>Holds the gate until the returned lease is disposed (use with using).</summary>
        public static IDisposable Hold(string reason)
        {
            lock (_gate)
            {
                _holders++;
                _reason = reason;
            }
            return new Lease();
        }

        private sealed class Lease : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _holders = Math.Max(0, _holders - 1);
                    if (_holders == 0) _reason = null;
                }
            }
        }
    }
}
