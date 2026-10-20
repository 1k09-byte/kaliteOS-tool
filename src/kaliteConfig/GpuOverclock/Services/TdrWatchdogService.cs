using System;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class TdrWatchdogService : ITdrWatchdog
    {
        private readonly object _gate = new();
        private Timer? _timer;
        private DateTime _windowStartUtc;
        private DateTime? _lastKnownEventTimeUtc;
        private bool _armed;
        private volatile bool _resetDetected;

        public event Action? DriverResetDetected;

        public void Arm()
        {
            lock (_gate)
            {
                _windowStartUtc = DateTime.UtcNow;
                _resetDetected = false;
                _armed = true;
                _lastKnownEventTimeUtc = TryReadLastTdrTimeUtc();
                _timer ??= new Timer(_ => OnTick(), null, 1500, 1500);
            }
        }

        public void Disarm()
        {
            lock (_gate)
            {
                _armed = false;
                _resetDetected = false;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public bool IsResetDetected
        {
            get { lock (_gate) return _resetDetected; }
        }

        private void OnTick()
        {
            Action? handler = null;
            lock (_gate)
            {
                if (!_armed) return;
                var t = TryReadLastTdrTimeUtc();
                if (t.HasValue && t.Value > _windowStartUtc)
                {
                    if (!_resetDetected)
                    {
                        _resetDetected = true;
                        handler = DriverResetDetected;
                    }
                }
            }
            handler?.Invoke();
        }

        public static DateTime? TryReadLastTdrTimeUtc()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            DateTime? best = null;

            var nv = QueryLatest("*[System[Provider[@Name='nvlddmkm'] and Level=2 and TimeCreated[timediff(@SystemTime) <= 86400000]]]");
            if (nv.HasValue && nv.Value > cutoff) best = nv;

            var tdr = QueryLatest("*[System[Provider[@Name='Display'] and TimeCreated[timediff(@SystemTime) <= 86400000]]]", requiredId: 4101);
            if (tdr.HasValue && tdr.Value > cutoff && (best is null || tdr.Value > best)) best = tdr;

            return best;
        }

        private static DateTime? QueryLatest(string xpath, int? requiredId = null)
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (reader.ReadEvent() is EventRecord rec)
                {
                    using (rec)
                    {
                        if (requiredId.HasValue && rec.Id != requiredId.Value) continue;
                        if (rec.TimeCreated.HasValue)
                            return rec.TimeCreated.Value.ToUniversalTime();
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
