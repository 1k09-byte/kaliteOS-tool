// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of this software, in any form, whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Persists favorites and recents to %LOCALAPPDATA%\kaliteConfig\Nvidia.
/// Kept separate from the pure state model so the format stays unit testable, and so a
/// failed write can never take the settings page down with it.
/// </summary>
public sealed class NvidiaSimpleSettingsStateStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public NvidiaSimpleSettingsStateStore(string? path = null)
        => _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Nvidia", "simple-settings-state.json");

    public string Path_ => _path;

    public NvidiaSimpleSettingsState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new NvidiaSimpleSettingsState();
                return NvidiaSimpleSettingsState.Deserialize(File.ReadAllText(_path));
            }
            catch (IOException) { return new NvidiaSimpleSettingsState(); }
            catch (UnauthorizedAccessException) { return new NvidiaSimpleSettingsState(); }
        }
    }

    public void Save(NvidiaSimpleSettingsState state)
    {
        lock (_gate)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, state.Serialize());
            }
            catch (IOException) { /* a lost favorite is not worth an error dialog */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
