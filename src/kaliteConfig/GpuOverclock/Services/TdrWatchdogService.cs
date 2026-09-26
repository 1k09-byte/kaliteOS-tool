// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Diagnostics.Eventing.Reader;
using System.Threading;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Detects Windows display-driver resets (TDR) after a write by watching
    /// the System event log for nvlddmkm/Display-source resets. Polling
    /// interval is short by design: it only runs while a confirmation window
    /// is armed, otherwise the watcher is idle.
    ///
    /// Mechanism note (spec 4.3), CONFIRMED on real hardware (RTX 4070 SUPER,
    /// driver 616.92): the classic System.Diagnostics.EventLog Entries reader
    /// silently returns nothing on .NET 10 even when matching events exist -
    /// the phase-2 verification caught this. The working channel is the
    /// EventLogReader (System.Diagnostics.Eventing.Reader) with an XPath query
    /// for nvlddmkm Level-2 errors (driver fault events) and Display/4101
    /// (TDR recovery), newest-first via ReverseDirection.
    /// </summary>
    public sealed class TdrWatchdogService : ITdrWatchdog
    {
        private readonly object _gate = new();
        private Timer? _timer;
        private DateTime _windowStartUtc;
        private DateTime? _lastKnownEventTimeUtc;
        private bool _armed;
        private volatile bool _resetDetected;

        /// <summary>Raised on a thread-pool thread when a reset is detected in the armed window.</summary>
        public event Action? DriverResetDetected;

        /// <summary>Begins watching for TDR events from now on.</summary>
        public void Arm()
        {
            lock (_gate)
            {
                _windowStartUtc = DateTime.UtcNow;
                _resetDetected = false;
                _armed = true;
                _lastKnownEventTimeUtc = TryReadLastTdrTimeUtc(); // baseline so pre-existing events don't trip it
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

        /// <summary>True if a reset was detected while armed (latched until Disarm).</summary>
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
                    // New TDR after the window opened - fire once.
                    if (!_resetDetected)
                    {
                        _resetDetected = true;
                        handler = DriverResetDetected;
                    }
                }
            }
            handler?.Invoke();
        }

        /// <summary>
        /// Reads the most recent TDR-recovery event: any nvlddmkm Error
        /// (driver fault) or Display/4101 (TDR recovery) within the last
        /// 24 hours. Null when none found or the log is unreadable.
        /// </summary>
        public static DateTime? TryReadLastTdrTimeUtc()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            DateTime? best = null;

            // nvlddmkm Level=2 (Error) - driver fault events. Query shape
            // verified live against the System log on .NET 10 (the combined
            // or-ed query below this one threw and returned null before the
            // split - the phase-2 harness caught it).
            var nv = QueryLatest("*[System[Provider[@Name='nvlddmkm'] and Level=2 and TimeCreated[timediff(@SystemTime) <= 86400000]]]");
            if (nv.HasValue && nv.Value > cutoff) best = nv;

            // Display/4101 = classic TDR-recovery event. Classic providers
            // record qualified IDs (e.g. 0x40000xxx), so EventID=4101 in XPath
            // does not reliably match; filter by provider and compare the
            // published record ID in code instead.
            var tdr = QueryLatest("*[System[Provider[@Name='Display'] and TimeCreated[timediff(@SystemTime) <= 86400000]]]", requiredId: 4101);
            if (tdr.HasValue && tdr.Value > cutoff && (best is null || tdr.Value > best)) best = tdr;

            return best;
        }

        /// <summary>Runs one newest-first XPath query; returns the newest matching timestamp. Null on none/error.</summary>
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
                // Log access can fail (permissions, log cleared mid-read);
                // fail silent-null so the watchdog never crashes the module.
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
