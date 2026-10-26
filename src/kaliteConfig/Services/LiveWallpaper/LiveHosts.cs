using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using WinRT.Interop;

namespace kaliteConfig.Services.LiveWallpaper;

public abstract class LiveHost : IDisposable
{
    private bool _disposed;

    protected LiveHost(MonitorRect area)
    {
        Area = area;
        View = new Window();
        Hwnd = WindowNative.GetWindowHandle(View);
        if (Hwnd == IntPtr.Zero)
        {
            View.Activate();
            Hwnd = WindowNative.GetWindowHandle(View);
        }
    }

    public MonitorRect Area { get; }
    public Window View { get; }
    public IntPtr Hwnd { get; }
    public bool IsAttached { get; set; }

    public abstract Task<(bool Ok, string? Error)> ShowAsync(LiveWallpaperItem item, LiveSettings settings);
    public abstract void ApplyPlayback(LiveSettings settings);
    public abstract void Pause();
    public abstract void Resume();
    public abstract void ReleaseHeavy();
    public abstract void Reacquire();

    public void SetVisible(bool visible)
    {
        try
        {
            if (Hwnd != IntPtr.Zero)
                DesktopHostNative.ShowWindow(Hwnd, visible ? DesktopHostNative.SW_SHOWNA : DesktopHostNative.SW_HIDE);
        }
        catch { }
    }

    public virtual void ShutdownMedia()
    {
        try
        {
            if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            {
                try { queue.TryEnqueue(ShutdownMedia); } catch { }
                return;
            }
            try { View.Content = null; } catch { }
        }
        catch { }
    }

    public void CloseWindowSoon(int delayMs = 400)
    {
        if (_disposed) return;
        try
        {
            var queue = View.DispatcherQueue;
            if (queue is null) { try { Dispose(); } catch { } return; }
            if (queue.HasThreadAccess)
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(delayMs).ConfigureAwait(false); } catch { }
                    try { queue.TryEnqueue(() => { try { Dispose(); } catch { } }); } catch { }
                });
                return;
            }
            try
            {
                queue.TryEnqueue(() =>
                {
                    _ = Task.Run(async () =>
                    {
                        try { await Task.Delay(delayMs).ConfigureAwait(false); } catch { }
                        try { queue.TryEnqueue(() => { try { Dispose(); } catch { } }); } catch { }
                    });
                });
            }
            catch { }
        }
        catch { try { Dispose(); } catch { } }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            {
                try { queue.TryEnqueue(() => { try { View.Close(); } catch { } }); } catch { }
                return;
            }
            try { View.Close(); } catch { }
        }
        catch { }
    }
}

public sealed class LiveVideoHost : LiveHost
{
    private MediaPlayerElement? _element;
    private MediaPlayer? _player;
    private bool _heavyReleased;
    private StorageFile? _file;
    private LiveSettings? _lastSettings;

    public LiveVideoHost(MonitorRect area) : base(area)
    {
    }

    public override async Task<(bool Ok, string? Error)> ShowAsync(LiveWallpaperItem item, LiveSettings settings)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path).AsTask().ConfigureAwait(false);
            return await ShowOnUiAsync(file, settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private Task<(bool Ok, string? Error)> ShowOnUiAsync(StorageFile file, LiveSettings settings)
    {
        _file = file;
        _lastSettings = settings;
        var tcs = new TaskCompletionSource<(bool Ok, string? Error)>();
        bool queued = false;
        try
        {
            queued = View.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    CreatePlayer(file, settings);
                    View.Content = _element;
                    View.Activate();
                    _player?.Play();
                    _heavyReleased = false;
                    tcs.SetResult((true, null));
                }
                catch (Exception ex)
                {
                    tcs.SetResult((false, ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            tcs.SetResult((false, ex.Message));
        }
        if (!queued) tcs.TrySetResult((false, "The wallpaper window is not ready."));
        return tcs.Task;
    }

    private void CreatePlayer(StorageFile file, LiveSettings settings)
    {
        ReleasePlayer();
        _player = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = settings.Muted,
            Volume = settings.Volume,
            AutoPlay = true
        };
        _player.PlaybackSession.PlaybackRate = settings.Rate <= 0 ? 1.0 : settings.Rate;
        _player.Source = MediaSource.CreateFromStorageFile(file);
        _player.MediaFailed += (_, _) => { };
        _element = new MediaPlayerElement();
        ApplyScale(settings.Scale);
        _element.SetMediaPlayer(_player);
    }

    private void ReleasePlayer()
    {
        try { View.Content = null; } catch { }
        try { _element?.SetMediaPlayer(null); } catch { }
        _element = null;
        var player = _player;
        _player = null;
        if (player is not null)
        {
            try { player.Pause(); } catch { }
            try { player.Source = null; } catch { }
            try { player.Dispose(); } catch { }
        }
    }

    public override void ShutdownMedia()
    {
        try
        {
            if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            {
                try { queue.TryEnqueue(ShutdownMedia); } catch { }
                return;
            }
            try { ReleasePlayer(); } catch { }
            _heavyReleased = false;
        }
        catch { }
    }

    public override void ApplyPlayback(LiveSettings settings)
    {
        _lastSettings = settings;
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ApplyScale(settings.Scale);
                    if (_player is null) return;
                    _player.IsMuted = settings.Muted;
                    _player.Volume = settings.Volume;
                    _player.PlaybackSession.PlaybackRate = settings.Rate <= 0 ? 1.0 : settings.Rate;
                }
                catch { }
            });
        }
        catch { }
    }

    private void ApplyScale(LiveScale scale)
    {
        if (_element is null) return;
        _element.Stretch = scale switch
        {
            LiveScale.Fit => Microsoft.UI.Xaml.Media.Stretch.Uniform,
            LiveScale.Stretch => Microsoft.UI.Xaml.Media.Stretch.Fill,
            LiveScale.Center => Microsoft.UI.Xaml.Media.Stretch.None,
            _ => Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
        };
    }

    public override void Pause()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try { _player?.Pause(); } catch { }
            });
        }
        catch { }
    }

    public override void Resume()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_heavyReleased) return;
                    _player?.Play();
                }
                catch { }
            });
        }
        catch { }
    }

    public override void ReleaseHeavy()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ReleasePlayer();
                    _heavyReleased = true;
                }
                catch { }
            });
        }
        catch { }
    }

    public override void Reacquire()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!_heavyReleased) return;
                    var file = _file;
                    var settings = _lastSettings;
                    if (file is null || settings is null) return;
                    _heavyReleased = false;
                    CreatePlayer(file, settings);
                    View.Content = _element;
                    try { _player?.Play(); } catch { }
                }
                catch { }
            });
        }
        catch { }
    }

    public override void Dispose()
    {
        try
        {
            if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            {
                try
                {
                    queue.TryEnqueue(() =>
                    {
                        try { ReleasePlayer(); } catch { }
                        try { View.Content = null; } catch { }
                        try { base.Dispose(); } catch { }
                    });
                }
                catch { }
                return;
            }
            try { ReleasePlayer(); } catch { }
            try { View.Content = null; } catch { }
        }
        catch { }
        base.Dispose();
    }
}

public sealed class LiveHtmlHost : LiveHost
{
    private const string VirtualHost = "live.local";

    private WebView2? _view;
    private string? _folder;
    private string? _entry;
    private LiveSettings? _settings;
    private bool _ready;

    public LiveHtmlHost(MonitorRect area) : base(area)
    {
    }

    public override async Task<(bool Ok, string? Error)> ShowAsync(LiveWallpaperItem item, LiveSettings settings)
    {
        try
        {
            string entry = "index.html";
            string folder = item.Path;
            var manifest = await Task.Run(() => ReadManifest(folder)).ConfigureAwait(false);
            if (manifest is not null) entry = manifest.Entry;
            else
            {
                string? found = await Task.Run(() => FindEntry(folder)).ConfigureAwait(false);
                if (found is null) return (false, "That package has no html entry page.");
                entry = found;
            }
            _folder = folder;
            _entry = entry;
            _settings = settings;
            var tcs = new TaskCompletionSource<(bool Ok, string? Error)>();
            bool queued = false;
            try
            {
                queued = View.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        _view = new WebView2();
                        View.Content = _view;
                        View.Activate();
                        var env = await CoreWebView2Environment.CreateAsync().AsTask().ConfigureAwait(true);
                        await _view.EnsureCoreWebView2Async(env).AsTask().ConfigureAwait(true);
                        Harden();
                        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(VirtualHost, _folder!,
                            CoreWebView2HostResourceAccessKind.DenyCors);
                        if (settings.FrameCap != LiveFrameCap.MatchDisplay)
                            await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FrameCapScript(settings.FrameCap));
                        _view.CoreWebView2.NavigationCompleted += (_, _) =>
                        {
                            _ready = true;
                            PostProperties();
                        };
                        _view.CoreWebView2.Navigate("https:" + "//" + VirtualHost + "/" + _entry!.Replace('\\', '/'));
                        tcs.SetResult((true, null));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetResult((false, ex.Message));
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult((false, ex.Message));
            }
            if (!queued) tcs.TrySetResult((false, "The wallpaper window is not ready."));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void PostProperties()
    {
        try
        {
            if (_view?.CoreWebView2 is null || !_ready || _settings is null) return;
            Dictionary<string, string>? values = null;
            if (_settings.PropertyValues.TryGetValue(_folder ?? string.Empty, out var saved)) values = saved;
            values ??= new Dictionary<string, string>();
            string json = JsonSerializer.Serialize(values);
            View.DispatcherQueue.TryEnqueue(async () =>
            {
                try { await _view.CoreWebView2.ExecuteScriptAsync("window.postMessage(" + json + ",\"*\")"); } catch { }
            });
        }
        catch { }
    }

    public override void ApplyPlayback(LiveSettings settings)
    {
        _settings = settings;
        PostProperties();
    }

    public override void Pause()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_view?.CoreWebView2 is not null)
                        await _view.CoreWebView2.TrySuspendAsync();
                }
                catch { }
            });
        }
        catch { }
    }

    public override void Resume()
    {
        try
        {
            View.DispatcherQueue.TryEnqueue(() =>
            {
                try { _view?.CoreWebView2?.Resume(); } catch { }
            });
        }
        catch { }
    }

    public override void ReleaseHeavy()
    {
    }

    public override void Reacquire()
    {
        Resume();
    }

    public override void ShutdownMedia()
    {
        try
        {
            var viewLocal = _view;
            _view = null;
            _ready = false;
            if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
            {
                try
                {
                    queue.TryEnqueue(() =>
                    {                            try
                            {
                                if (viewLocal is not null)
                                {
                                    try { viewLocal.Close(); } catch { }
                                }
                            }
                            catch { }
                            try { View.Content = null; } catch { }
                    });
                }
                catch { }
                return;
            }
            try
            {
                if (viewLocal is not null)
                {
                    try { viewLocal.Close(); } catch { }
                }
            }
            catch { }
            try { View.Content = null; } catch { }
        }
        catch { }
    }

    public override void Dispose()
    {
        try
        {
            var view = _view;
            _view = null;
            _ready = false;
            if (view is not null)
            {
                try
                {
                    if (View.DispatcherQueue is { } queue && !queue.HasThreadAccess)
                    {
                        try
                        {
                            queue.TryEnqueue(() =>
                            {
                                try { view.Close(); } catch { }
                                try { View.Content = null; } catch { }
                                try { base.Dispose(); } catch { }
                            });
                        }
                        catch { }
                        return;
                    }
                    try { view.Close(); } catch { }
                    try { View.Content = null; } catch { }
                }
                catch { }
            }
            else
            {
                try { View.Content = null; } catch { }
            }
        }
        catch { }
        base.Dispose();
    }

    private void Harden()
    {
        var core = _view!.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            try
            {
                if (e.Uri is null || !e.Uri.StartsWith("https:" + "//" + VirtualHost + "/", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            }
            catch { e.Cancel = true; }
        };
        core.NewWindowRequested += (_, e) =>
        {
            try { e.Handled = true; } catch { }
        };
        core.DownloadStarting += (_, e) =>
        {
            try { e.Cancel = true; } catch { }
        };
        core.PermissionRequested += (_, e) =>
        {
            try { e.State = CoreWebView2PermissionState.Deny; } catch { }
        };
        core.ScriptDialogOpening += (_, e) =>
        {
            try { e.Accept(); } catch { }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                var uri = e.Request?.Uri;
                if (uri is null || !uri.StartsWith("https:" + "//" + VirtualHost + "/", StringComparison.OrdinalIgnoreCase))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
            catch { }
        };
    }

    private static string FrameCapScript(LiveFrameCap cap)
    {
        double gap = cap switch
        {
            LiveFrameCap.Fps60 => 16.7,
            LiveFrameCap.Fps30 => 33.4,
            LiveFrameCap.Fps15 => 66.7,
            _ => 16.7
        };
        string gapText = gap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "(function(){var g=" + gapText + ";var l=0;var f=window.requestAnimationFrame;window.requestAnimationFrame=function(c){return f(function(t){var n=performance.now();if(n-l>=g){l=n;try{c(t);}catch(e){}}});};})();";
    }

    private static LiveManifest? ReadManifest(string folder)
    {
        try
        {
            string path = System.IO.Path.Combine(folder, "manifest.json");
            if (!System.IO.File.Exists(path)) return null;
            var service = new WallpaperPackageService();
            var (manifest, _) = service.TryParseManifest(System.IO.File.ReadAllText(path), folder);
            return manifest;
        }
        catch { return null; }
    }

    private static string? FindEntry(string folder)
    {
        try
        {
            foreach (var candidate in new[] { "index.html", "index.htm", "wallpaper.html", "main.html" })
                if (System.IO.File.Exists(System.IO.Path.Combine(folder, candidate))) return candidate;
            var first = System.IO.Directory.EnumerateFiles(folder, "*.html", System.IO.SearchOption.TopDirectoryOnly);
            foreach (var f in first) return System.IO.Path.GetFileName(f);
        }
        catch { }
        return null;
    }
}
