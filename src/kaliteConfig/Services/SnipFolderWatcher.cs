// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using System.Threading;

namespace kaliteConfig.Services;

/// <summary>
/// Tells the page the moment the snips folder changes, so the poll can be re-armed instead of
/// waiting out the lazy end of its interval.
///
/// This is a latency optimisation only. Polling alone still finds every change, so a watcher that
/// refuses to start (no folder, policy, quota) degrades to a slower pick-up and nothing else.
/// Events arrive on a pool thread and are debounced, because one save is several events
/// (create, write, size, last-write) and the caller only needs "something moved".
/// </summary>
public sealed class SnipFolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly TimeSpan _debounceDelay;
    private readonly Action _onChanged;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private int _pending;
    private bool _disposed;

    private SnipFolderWatcher(string folder, Action onChanged, TimeSpan debounceDelay, Action<string>? log)
    {
        _onChanged = onChanged;
        _debounceDelay = debounceDelay;
        _log = log ?? LogToDebug;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
            };
            _watcher.Created += (_, _) => Raise();
            _watcher.Changed += (_, _) => Raise();
            _watcher.Deleted += (_, _) => Raise();
            _watcher.Renamed += (_, _) => Raise();
            _watcher.Error += (_, e) => _log($"folder watch error: {e.GetException()?.Message}");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _watcher = null;
            _log($"folder watch unavailable, polling only: {ex.Message}");
        }
    }

    private static void LogToDebug(string message) => System.Diagnostics.Debug.WriteLine(message);

    /// <summary>True when the folder is being watched; false means polling is carrying the page alone.</summary>
    public bool IsWatching => _watcher is not null;

    public static SnipFolderWatcher Start(string folder, Action onChanged, TimeSpan? debounce = null,
        Action<string>? log = null)
        => new(folder, onChanged, debounce ?? TimeSpan.FromMilliseconds(300), log);

    private void Raise()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_disposed) return;
            // One pending notification is enough: the caller re-reads the folder, not the event.
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            try { _debounce.Change(_debounceDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void Fire()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_disposed) return;
        try { _onChanged(); }
        catch (Exception ex) { _log($"folder watch callback failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { if (_watcher is not null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
        try { _debounce.Dispose(); } catch { }
    }
}
