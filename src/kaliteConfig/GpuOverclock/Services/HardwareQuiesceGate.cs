using System;

namespace kaliteConfig.GpuOverclock.Services
{
    public static class HardwareQuiesceGate
    {
        private static readonly object _gate = new();
        private static int _holders;
        private static string? _reason;

        public static bool IsQuiesced
        {
            get { lock (_gate) return _holders > 0; }
        }

        public static string? Reason
        {
            get { lock (_gate) return _reason; }
        }

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
