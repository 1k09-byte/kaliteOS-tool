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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace kaliteConfig.Services;

public enum CueKind
{
    Tick,
    Started,
    TenLeft,
    Stopped,
    Saved,
    Failed,
}

public enum CueMode
{
    Beeps,
    Voice,
    Off,
}

public sealed class CueService
{
    private static readonly Lazy<CueService> _lazy = new(() => new CueService());
    public static CueService Instance => _lazy.Value;

    private static readonly Dictionary<CueKind, string> BeepFiles = new()
    {
        [CueKind.Tick] = "tick.wav",
        [CueKind.Started] = "start.wav",
        [CueKind.TenLeft] = "tenleft.wav",
        [CueKind.Stopped] = "stop.wav",
        [CueKind.Saved] = "saved.wav",
        [CueKind.Failed] = "failed.wav",
    };

    private readonly Dictionary<CueKind, (MediaPlayer Player, string Path)> _players = new();
    private readonly object _gate = new();
    private CueMode _mode = CueMode.Beeps;
    private double _volume = 0.8;

    public event Action? SettingsChanged;

    public CueMode Mode
    {
        get { lock (_gate) return _mode; }
        set { lock (_gate) _mode = value; Save(); SettingsChanged?.Invoke(); }
    }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            double v = Math.Clamp(value, 0, 1);
            lock (_gate)
            {
                _volume = v;
                foreach (var entry in _players.Values)
                    try { entry.Player.Volume = v; } catch { }
            }
            Save();
            SettingsChanged?.Invoke();
        }
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "benchmark-cues.json");

    private sealed class CueSettings
    {
        public CueMode Mode { get; set; } = CueMode.Beeps;
        public double Volume { get; set; } = 0.8;
    }

    private CueService()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<CueSettings>(File.ReadAllText(SettingsPath));
                if (s != null)
                {
                    _mode = s.Mode;
                    _volume = Math.Clamp(s.Volume, 0, 1);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new CueSettings { Mode = _mode, Volume = _volume }));
        }
        catch { }
    }

    public static string SoundDir =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");

    public static bool VoiceAvailable =>
        VoiceFilePresent("voice_started") && VoiceFilePresent("voice_stopped");

    private static bool VoiceFilePresent(string stem) =>
        File.Exists(Path.Combine(SoundDir, stem + ".wav")) ||
        File.Exists(Path.Combine(SoundDir, stem + ".mp3"));

    private string ResolveFile(CueKind kind)
    {
        CueMode mode;
        lock (_gate) mode = _mode;
        if (mode == CueMode.Voice && kind is CueKind.Started or CueKind.Stopped or CueKind.Failed or CueKind.Saved)
        {
            string stem = kind switch
            {
                CueKind.Started => "voice_started",
                CueKind.Stopped => "voice_stopped",
                CueKind.Failed => "voice_failed",
                _ => "voice_saved",
            };
            string wav = Path.Combine(SoundDir, stem + ".wav");
            if (File.Exists(wav)) return wav;
            string mp3 = Path.Combine(SoundDir, stem + ".mp3");
            if (File.Exists(mp3)) return mp3;
        }
        return Path.Combine(SoundDir, BeepFiles[kind]);
    }

    public void Play(CueKind kind)
    {
        CueMode mode;
        double vol;
        lock (_gate) { mode = _mode; vol = _volume; }
        if (mode == CueMode.Off) return;
        try
        {
            string path = ResolveFile(kind);
            if (!File.Exists(path)) return;
            MediaPlayer player;
            lock (_gate)
            {
                if (!_players.TryGetValue(kind, out var entry) || entry.Player == null || entry.Path != path)
                {
                    try { entry.Player?.Dispose(); } catch { }
                    player = new MediaPlayer { Volume = vol };
                    string fallback = Path.Combine(SoundDir, BeepFiles[kind]);
                    player.MediaFailed += (_, _) =>
                    {
                        try
                        {
                            if (!string.Equals(path, fallback, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(fallback))
                            {
                                player.Source = MediaSource.CreateFromUri(new Uri(fallback));
                                player.Play();
                            }
                        }
                        catch { }
                    };
                    player.Source = MediaSource.CreateFromUri(new Uri(path));
                    _players[kind] = (player, path);
                    try { player.Play(); } catch { }
                    return;
                }
                player = entry.Player;
                player.Volume = vol;
            }
            try
            {
                player.Pause();
                try { player.PlaybackSession.Position = TimeSpan.Zero; } catch { }
                player.Play();
            }
            catch { }
        }
        catch { }
    }
}
