using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace kaliteConfig.Services.LiveWallpaper;

public interface ILiveWallpaperEngine
{
    LiveState State { get; }
    LivePauseReasons ActivePauses { get; }
    LiveSettings CurrentSettings { get; }
    string StatusDetail { get; }
    LiveWallpaperItem? CurrentItem { get; }
    event Action? Changed;
    bool CanStart(out string reason);
    Task<(bool Ok, string? Error)> StartAsync(IReadOnlyList<(MonitorRect Area, LiveWallpaperItem Item)> targets);
    void Pause();
    void Resume();
    void ApplySettings(LiveSettings settings);
    void StopAndRestore();
    Task<(bool Ok, string? Error)> TryResumeOnLaunchAsync();
    void PlaylistNext();
}

public sealed class LiveWallpaperEngine : ILiveWallpaperEngine
{
    private readonly IDesktopHostService _hosts;
    private readonly IWallpaperPowerMonitor _monitor;
    private readonly ILiveLibraryService _library;
    private readonly LiveWallpaperStore _store = new();
    private readonly Random _random = new();

    private readonly List<LiveHost> _active = new();
    private LiveSettings _settings;
    private DispatcherQueue? _queue;
    private DispatcherQueueTimer? _monitorTimer;
    private DispatcherQueueTimer? _playlistTimer;
    private DispatcherQueueTimer? _heavyTimer;
    private bool _userPaused;
    private bool _monitorPaused;
    private bool _heavyReleased;
    private int _playlistIndex;

    public LiveWallpaperEngine(
        IDesktopHostService hosts,
        IWallpaperPowerMonitor monitor,
        ILiveLibraryService library)
    {
        _hosts = hosts;
        _monitor = monitor;
        _library = library;
        _settings = _store.LoadSettings();
    }

    public LiveState State { get; private set; } = LiveState.Stopped;
    public LiveSettings CurrentSettings => _settings;
    public LivePauseReasons ActivePauses { get; private set; } = LivePauseReasons.None;
    public LiveWallpaperItem? CurrentItem { get; private set; }
    public event Action? Changed;

    public string StatusDetail
    {
        get
        {
            return State switch
            {
                LiveState.Playing => CurrentItem is null ? "Playing." : "Playing " + CurrentItem.Name + ".",
                LiveState.Paused => "Paused" + PauseWhy() + ".",
                LiveState.Starting => "Starting…",
                LiveState.Failed => "Failed to start.",
                _ => "Stopped.",
            };
        }
    }

    private string PauseWhy()
    {
        if (ActivePauses == LivePauseReasons.None) return string.Empty;
        var parts = new List<string>();
        if (ActivePauses.HasFlag(LivePauseReasons.Fullscreen)) parts.Add("a fullscreen app is in the foreground");
        if (ActivePauses.HasFlag(LivePauseReasons.Battery)) parts.Add("the PC is on battery power");
        if (ActivePauses.HasFlag(LivePauseReasons.LockedOrDisplayOff)) parts.Add("the session is locked or the display is off");
        if (ActivePauses.HasFlag(LivePauseReasons.EnergySaver)) parts.Add("Energy Saver is active");
        if (ActivePauses.HasFlag(LivePauseReasons.RemoteSession)) parts.Add("this is a remote session");
        if (ActivePauses.HasFlag(LivePauseReasons.Covered)) parts.Add("the desktop is fully covered");
        return " (" + string.Join(", ", parts) + ")";
    }

    public bool CanStart(out string reason)
    {
        if (!_settings.FeatureEnabled)
        {
            reason = "Live wallpaper is off. Turn it on to start.";
            return false;
        }
        if (!_hosts.CanAttach(out reason)) return false;
        reason = string.Empty;
        return true;
    }

    public async Task<(bool Ok, string? Error)> StartAsync(IReadOnlyList<(MonitorRect Area, LiveWallpaperItem Item)> targets)
    {
        if (!CanStart(out string gate))
            return (false, gate);
        if (targets.Count == 0)
            return (false, "There is nothing to play on.");
        _queue = DispatcherQueue.GetForCurrentThread();
        try { StopHosts(); } catch { }
        State = LiveState.Starting;
        Notify();
        try
        {
        foreach (var (area, item) in targets)
        {
            if (item.Kind == LiveKind.Video && !System.IO.File.Exists(item.Path))
            {
                StopHosts();
                State = LiveState.Failed;
                Notify();
                return (false, "The video file is missing: " + item.Path + ". Remove it from the library and add it again.");
            }
            if (item.Kind == LiveKind.Html && !System.IO.Directory.Exists(item.Path))
            {
                StopHosts();
                State = LiveState.Failed;
                Notify();
                return (false, "The HTML folder is missing: " + item.Path + ". Remove it from the library and add it again.");
            }
            LiveHost host = item.Kind == LiveKind.Video
                ? new LiveVideoHost(area)
                : new LiveHtmlHost(area);
            var (ok, error) = await host.ShowAsync(item, _settings).ConfigureAwait(true);
            string? attachError = null;
            if (!ok || !_hosts.TryAttach(host.Hwnd, area, out attachError))
            {
                try { host.Dispose(); } catch { }
                StopHosts();
                State = LiveState.Failed;
                Notify();
                return (false, error ?? attachError ?? "The wallpaper could not be placed behind the desktop icons.");
            }
            _active.Add(host);
        }
        }
        catch (Exception ex)
        {
            try { StopHosts(); } catch { }
            State = LiveState.Failed;
            Notify();
            return (false, "The wallpaper could not start: " + ex.Message);
        }
        CurrentItem = targets[0].Item;
        _userPaused = false;
        _monitorPaused = false;
        _heavyReleased = false;
        ActivePauses = LivePauseReasons.None;
        State = LiveState.Playing;
        _settings = _settings with { LastItemId = CurrentItem.Id };
        _store.SaveSettings(_settings);
        StartTimers();
        Notify();
        return (true, null);
    }

    public void Pause()
    {
        if (State != LiveState.Playing) return;
        _userPaused = true;
        foreach (var host in _active)
        {
            try { host.Pause(); } catch { }
        }
        State = LiveState.Paused;
        Notify();
    }

    public void Resume()
    {
        if (State != LiveState.Paused || !_userPaused) return;
        var reasons = EvaluatePauses();
        if (reasons != LivePauseReasons.None)
        {
            _userPaused = false;
            _monitorPaused = true;
            ActivePauses = reasons;
            Notify();
            return;
        }
        _userPaused = false;
        ReacquireAll();
        foreach (var host in _active)
        {
            try { host.Resume(); } catch { }
        }
        State = LiveState.Playing;
        Notify();
    }

    public void ApplySettings(LiveSettings settings)
    {
        _settings = settings;
        _store.SaveSettings(settings);
        _monitor.PauseOnFullscreen = settings.PauseOnFullscreen;
        _monitor.PauseOnBattery = settings.PauseOnBattery;
        _monitor.PauseOnEnergySaver = settings.PauseOnEnergySaver;
        _monitor.CheckCovered = settings.PauseWhenCovered;
        foreach (var host in _active)
        {
            try { host.ApplyPlayback(settings); } catch { }
        }
        RestartPlaylistTimer();
        Notify();
    }

    public void StopAndRestore()
    {
        try
        {
        if (_queue is not null && !_queue.HasThreadAccess)
        {
            try { _queue.TryEnqueue(StopAndRestore); } catch { }
            return;
        }
        try { StopTimers(); } catch { }
        try { StopHosts(); } catch { }
        CurrentItem = null;
        _userPaused = false;
        _monitorPaused = false;
        _heavyReleased = false;
        ActivePauses = LivePauseReasons.None;
        if (State != LiveState.Stopped)
        {
            State = LiveState.Stopped;
            Notify();
        }
        }
        catch { }
    }

    public async Task<(bool Ok, string? Error)> TryResumeOnLaunchAsync()
    {
        var settings = _store.LoadSettings();
        _settings = settings;
        if (!settings.FeatureEnabled || string.IsNullOrEmpty(settings.LastItemId))
            return (true, null);
        var item = _library.List().FirstOrDefault(i => i.Id == settings.LastItemId);
        if (item is null) return (true, null);
        var areas = _hosts.GetMonitors();
        var targets = areas.Select(a => (a, item)).ToList();
        return await StartAsync(targets).ConfigureAwait(true);
    }

    public void PlaylistNext()
    {
        if (State != LiveState.Playing && State != LiveState.Paused) return;
        var items = _library.List();
        if (items.Count == 0) return;
        LiveWallpaperItem next;
        if (_settings.PlaylistShuffle)
            next = items[_random.Next(items.Count)];
        else
        {
            _playlistIndex = (_playlistIndex + 1) % items.Count;
            next = items[_playlistIndex];
        }
        var areas = _hosts.GetMonitors();
        var targets = areas.Select(a => (a, next)).ToList();
        _ = StartAsync(targets);
    }

    private void StartTimers()
    {
        StopTimers();
        if (_queue is null) return;
        _monitor.PauseOnFullscreen = _settings.PauseOnFullscreen;
        _monitor.PauseOnBattery = _settings.PauseOnBattery;
        _monitor.PauseOnEnergySaver = _settings.PauseOnEnergySaver;
        _monitor.CheckCovered = _settings.PauseWhenCovered;
        _monitorTimer = _queue.CreateTimer();
        _monitorTimer.Interval = TimeSpan.FromSeconds(5);
        _monitorTimer.Tick += (_, _) => MonitorTick();
        _monitorTimer.Start();
        RestartPlaylistTimer();
    }

    private void RestartPlaylistTimer()
    {
        try { _playlistTimer?.Stop(); } catch { }
        _playlistTimer = null;
        if (_queue is null || !_settings.PlaylistEnabled) return;
        _playlistTimer = _queue.CreateTimer();
        _playlistTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PlaylistMinutes));
        _playlistTimer.Tick += (_, _) => PlaylistNext();
        _playlistTimer.Start();
    }

    private void StopTimers()
    {
        foreach (var timer in new[] { _monitorTimer, _playlistTimer, _heavyTimer })
        {
            try { timer?.Stop(); } catch { }
        }
        _monitorTimer = null;
        _playlistTimer = null;
        _heavyTimer = null;
    }

    private void MonitorTick()
    {
        try
        {
            if (State != LiveState.Playing && State != LiveState.Paused) return;
            if (_userPaused) return;
            var reasons = EvaluatePauses();
            if (State == LiveState.Playing && reasons != LivePauseReasons.None)
            {
                _monitorPaused = true;
                ActivePauses = reasons;
                foreach (var host in _active)
                {
                    try { host.Pause(); } catch { }
                }
                State = LiveState.Paused;
                ArmHeavyTimer();
                Notify();
                return;
            }
            if (State == LiveState.Paused && _monitorPaused)
            {
                if (reasons == LivePauseReasons.None)
                {
                    _monitorPaused = false;
                    ActivePauses = LivePauseReasons.None;
                    DisarmHeavyTimer();
                    ReacquireAll();
                    foreach (var host in _active)
                    {
                        try { host.Resume(); } catch { }
                    }
                    State = LiveState.Playing;
                    Notify();
                }
                else if (reasons != ActivePauses)
                {
                    ActivePauses = reasons;
                    Notify();
                }
                return;
            }
            if (State == LiveState.Playing && !VerifyAll())
            {
                if (!ReattachAll())
                {
                    StopHosts();
                    State = LiveState.Failed;
                    Notify();
                }
            }
        }
        catch { }
    }

    private LivePauseReasons EvaluatePauses()
    {
        try
        {
            var cover = CoverUnion();
            return _monitor.Evaluate(cover);
        }
        catch { return LivePauseReasons.None; }
    }

    private RectInt32 CoverUnion()
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;
        foreach (var host in _active)
        {
            var a = host.Area;
            if (a.X < left) left = a.X;
            if (a.Y < top) top = a.Y;
            if (a.X + a.Width > right) right = a.X + a.Width;
            if (a.Y + a.Height > bottom) bottom = a.Y + a.Height;
        }
        if (left == int.MaxValue) return new RectInt32 { X = 0, Y = 0, Width = 0, Height = 0 };
        return new RectInt32 { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    private bool VerifyAll()
    {
        foreach (var host in _active)
        {
            try
            {
                if (!_hosts.Verify(host.Hwnd, host.Area)) return false;
            }
            catch { return false; }
        }
        return _active.Count > 0;
    }

    private bool ReattachAll()
    {
        foreach (var host in _active)
        {
            try
            {
                if (!_hosts.TryAttach(host.Hwnd, host.Area, out _)) return false;
            }
            catch { return false; }
        }
        return true;
    }

    private void ArmHeavyTimer()
    {
        DisarmHeavyTimer();
        if (_queue is null) return;
        _heavyTimer = _queue.CreateTimer();
        _heavyTimer.Interval = TimeSpan.FromSeconds(30);
        _heavyTimer.Tick += (_, _) =>
        {
            DisarmHeavyTimer();
            if (State != LiveState.Paused) return;
            _heavyReleased = true;
            foreach (var host in _active)
            {
                try { host.ReleaseHeavy(); } catch { }
                try { host.SetVisible(false); } catch { }
            }
        };
        _heavyTimer.Start();
    }

    private void DisarmHeavyTimer()
    {
        try { _heavyTimer?.Stop(); } catch { }
        _heavyTimer = null;
    }

    private void ReacquireAll()
    {
        if (!_heavyReleased) return;
        _heavyReleased = false;
        foreach (var host in _active)
        {
            try { host.SetVisible(true); } catch { }
            try { host.Reacquire(); } catch { }
        }
    }

    private void StopHosts()
    {
        foreach (var host in _active)
        {
            try { _hosts.Detach(host.Hwnd); } catch { }
            try { host.Dispose(); } catch { }
        }
        _active.Clear();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
