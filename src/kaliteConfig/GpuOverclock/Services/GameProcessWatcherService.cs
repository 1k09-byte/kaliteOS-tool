using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class GameProcessWatcherService : IDisposable
    {
        public sealed record ProcessEventInfo(string ProcessName, int ProcessId, string? FullPath);

        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(2500);

        private readonly object _gate = new();
        private HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ManagementEventWatcher> _wmiWatchers = new();
        private Timer? _pollTimer;
        private Dictionary<int, string> _knownPids = new();
        private bool _running;
        private bool _disposed;

        public bool UsingEventDriven { get; private set; }

        public string? LastError { get; private set; }

        public bool IsRunning { get { lock (_gate) return _running; } }

        public string ModeDescription => !IsRunning ? "watcher stopped"
            : UsingEventDriven ? $"event-driven (WMI process trace, {_wmiWatchers.Count / 2} executable(s))"
            : $"polling fallback every {(int)PollInterval.TotalMilliseconds} ms ({LastError})";

        public void SetWatchedExecutables(IEnumerable<string> executableNames)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in executableNames)
            {
                var norm = Normalize(n);
                if (norm.Length > 0) set.Add(norm);
            }
            bool restart;
            lock (_gate)
            {
                restart = _running && !set.SetEquals(_watched);
                _watched = set;
            }
            if (restart) Restart();
        }

        public static string Normalize(string? name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "";
            try
            {
                var file = System.IO.Path.GetFileName(n);
                if (!string.IsNullOrEmpty(file)) n = file;
            }
            catch { }
            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
            return n;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _running) return;
                _running = true;
            }
            EstablishMechanism();
            foreach (var proc in SnapshotWatchedRunning())
                RaiseStarted(proc);
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
            }
            TearDownMechanism();
        }

        private void Restart()
        {
            TearDownMechanism();
            lock (_gate) { if (!_running) return; }
            EstablishMechanism();
        }

        private void EstablishMechanism()
        {
            List<string> names;
            lock (_gate) names = _watched.ToList();
            if (TryStartWmi(names)) return;
            StartPolling();
        }

        private bool TryStartWmi(IReadOnlyList<string> names)
        {
            var started = new List<ManagementEventWatcher>();
            try
            {
                foreach (var name in names)
                {
                    var lit = "'" + name.Replace("'", "''") + "'";
                    var startWatcher = new ManagementEventWatcher(
                        new WqlEventQuery($"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = {lit}"));
                    startWatcher.EventArrived += (_, e) => OnWmiStart(e);
                    startWatcher.Start();
                    started.Add(startWatcher);

                    var stopWatcher = new ManagementEventWatcher(
                        new WqlEventQuery($"SELECT * FROM Win32_ProcessStopTrace WHERE ProcessName = {lit}"));
                    stopWatcher.EventArrived += (_, e) => OnWmiStop(e);
                    stopWatcher.Start();
                    started.Add(stopWatcher);
                }
                lock (_gate)
                {
                    _wmiWatchers.Clear();
                    _wmiWatchers.AddRange(started);
                    UsingEventDriven = true;
                    LastError = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                foreach (var w in started) { try { w.Stop(); w.Dispose(); } catch { } }
                lock (_gate)
                {
                    UsingEventDriven = false;
                    LastError = $"WMI process trace unavailable ({ex.GetType().Name}); using polling";
                }
                return false;
            }
        }

        private void OnWmiStart(EventArrivedEventArgs e)
        {
            try
            {
                var name = e.NewEvent.Properties["ProcessName"]?.Value as string ?? "";
                var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"]?.Value ?? 0);
                if (pid <= 0 || name.Length == 0) return;
                lock (_gate)
                {
                    if (!_running) return;
                    _knownPids[pid] = name;
                }
                RaiseStarted(new ProcessEventInfo(name, pid, TryGetProcessPath(pid)));
            }
            catch {  }
        }

        private void OnWmiStop(EventArrivedEventArgs e)
        {
            try
            {
                var name = e.NewEvent.Properties["ProcessName"]?.Value as string ?? "";
                var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"]?.Value ?? 0);
                if (pid <= 0) return;
                string? recordedPath = null;
                lock (_gate)
                {
                    if (!_running) return;
                    if (_knownPids.TryGetValue(pid, out var known)) name = known;
                    _knownPids.Remove(pid);
                }
                if (name.Length == 0) return;
                RaiseStopped(new ProcessEventInfo(name, pid, recordedPath));
            }
            catch { }
        }

        private void StartPolling()
        {
            lock (_gate)
            {
                _knownPids = SnapshotAllPids();
                _pollTimer ??= new Timer(_ => OnPollTick(), null, PollInterval, PollInterval);
                _pollTimer.Change(PollInterval, PollInterval);
            }
        }

        private void OnPollTick()
        {
            Dictionary<int, string> current;
            try { current = SnapshotAllPids(); }
            catch { return; }

            List<ProcessEventInfo> started = new(), stopped = new();
            HashSet<string> watched;
            lock (_gate)
            {
                if (!_running) return;
                watched = new HashSet<string>(_watched, StringComparer.OrdinalIgnoreCase);
                foreach (var (pid, name) in current)
                    if (!_knownPids.ContainsKey(pid) && watched.Contains(Normalize(name)))
                        started.Add(new ProcessEventInfo(name, pid, null));
                foreach (var (pid, name) in _knownPids)
                    if (!current.ContainsKey(pid) && watched.Contains(Normalize(name)))
                        stopped.Add(new ProcessEventInfo(name, pid, null));
                _knownPids = current;
            }
            foreach (var s in started)
                RaiseStarted(s with { FullPath = TryGetProcessPath(s.ProcessId) });
            foreach (var s in stopped)
                RaiseStopped(s);
        }

        private void TearDownMechanism()
        {
            List<ManagementEventWatcher> wmi;
            Timer? poll;
            lock (_gate)
            {
                wmi = _wmiWatchers.ToList();
                _wmiWatchers.Clear();
                poll = _pollTimer;
                _pollTimer = null;
                UsingEventDriven = false;
            }
            foreach (var w in wmi) { try { w.Stop(); w.Dispose(); } catch { } }
            try { poll?.Dispose(); } catch { }
        }

        private static Dictionary<int, string> SnapshotAllPids()
        {
            var map = new Dictionary<int, string>();
            foreach (var p in Process.GetProcesses())
            {
                try { map[p.Id] = p.ProcessName; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return map;
        }

        private List<ProcessEventInfo> SnapshotWatchedRunning()
        {
            HashSet<string> watched;
            lock (_gate) watched = new HashSet<string>(_watched, StringComparer.OrdinalIgnoreCase);
            var list = new List<ProcessEventInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var norm = Normalize(p.ProcessName);
                    if (watched.Contains(norm))
                    {
                        int pid = p.Id;
                        string name = p.ProcessName;
                        list.Add(new ProcessEventInfo(name, pid, null));
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            lock (_gate)
            {
                foreach (var e in list) _knownPids[e.ProcessId] = e.ProcessName;
            }
            return list;
        }

        public static string? TryGetProcessPath(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private void RaiseStarted(ProcessEventInfo e)
        {
            try { ProcessStarted?.Invoke(e); } catch { }
        }

        private void RaiseStopped(ProcessEventInfo e)
        {
            try { ProcessStopped?.Invoke(e); } catch { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _running = false;
            }
            TearDownMechanism();
        }
    }
}
