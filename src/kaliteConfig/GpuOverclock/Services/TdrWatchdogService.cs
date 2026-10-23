using System;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class TdrWatchdogService : ITdrWatchdog
    {
        // Each poll used to run two reverse scans over the last 24 h of System
        // log events every 1.5 s. One merged provider query bounded to the arm
        // window costs a fraction of that, and a 5 s cadence still lands well
        // inside the safety confirmation countdown that consumes the result.
        private static readonly TimeSpan FirstPollDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan QuerySlack = TimeSpan.FromMinutes(1);

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
                _timer ??= new Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
                // Disarm() parks the timer, so without this Change the second
                // and every later Arm() in a session stopped watching for
                // driver resets entirely.
                _timer.Change(FirstPollDelay, PollPeriod);
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
                var t = TryReadLastTdrTimeUtc(_windowStartUtc - QuerySlack);
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
            return TryReadLastTdrTimeUtc(DateTime.UtcNow - TimeSpan.FromHours(24));
        }

        private static DateTime? TryReadLastTdrTimeUtc(DateTime lowerBoundUtc)
        {
            var hardFloor = DateTime.UtcNow - TimeSpan.FromHours(24);
            if (lowerBoundUtc < hardFloor) lowerBoundUtc = hardFloor;
            var maxAgeMs = (long)(DateTime.UtcNow - lowerBoundUtc).TotalMilliseconds;
            return QueryLatest(maxAgeMs, lowerBoundUtc);
        }

        private static DateTime? QueryLatest(long maxAgeMs, DateTime lowerBoundUtc)
        {
            try
            {
                // One reverse scan for both sources instead of two: nvlddmkm
                // error events (Level=2) and the Display provider's TDR (4101)
                // are told apart by the post-filters below. The timediff bound
                // keeps the event log from walking a day of history per poll.
                var xpath = "*[System[(Provider[@Name='nvlddmkm'] or Provider[@Name='Display'])" +
                    $" and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";
                var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (reader.ReadEvent() is EventRecord rec)
                {
                    using (rec)
                    {
                        if (!rec.TimeCreated.HasValue) continue;
                        var t = rec.TimeCreated.Value.ToUniversalTime();
                        if (t < lowerBoundUtc) break;
                        if (string.Equals(rec.ProviderName, "nvlddmkm", StringComparison.OrdinalIgnoreCase))
                        {
                            if (rec.Level == 2) return t;
                        }
                        else if (rec.Id == 4101)
                        {
                            return t;
                        }
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
