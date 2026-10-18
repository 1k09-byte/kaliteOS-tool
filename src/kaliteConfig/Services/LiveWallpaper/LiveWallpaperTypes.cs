using System;
using System.Collections.Generic;

namespace kaliteConfig.Services.LiveWallpaper;

public enum LiveKind
{
    Video,
    Html
}

public enum LiveState
{
    Stopped,
    Starting,
    Playing,
    Paused,
    Failed
}

public enum LiveTargetMode
{
    SameOnAll,
    PerMonitor,
    SpanAll
}

[Flags]
public enum LivePauseReasons
{
    None = 0,
    Fullscreen = 1,
    Battery = 2,
    LockedOrDisplayOff = 4,
    EnergySaver = 8,
    RemoteSession = 16,
    Covered = 32
}

public enum LiveScale
{
    Fill,
    Fit,
    Stretch,
    Center
}

public enum LiveFrameCap
{
    MatchDisplay,
    Fps60,
    Fps30,
    Fps15
}

public sealed record LiveWallpaperItem(
    string Id,
    LiveKind Kind,
    string Name,
    string Path,
    string? PreviewPath,
    DateTime AddedUtc);

public sealed record LivePropertyDef(
    string Key,
    string Type,
    string Label,
    double Min,
    double Max,
    double Step,
    string Default,
    string[] Options);

public sealed record LiveManifest(
    int SchemaVersion,
    string Name,
    string Type,
    string Entry,
    string? Preview,
    Dictionary<string, LivePropertyDef>? Properties);

public sealed record MonitorTarget(string MonitorId, string ItemId);

public sealed record LiveSettings(
    bool FeatureEnabled,
    bool ResumeOnLaunch,
    LiveTargetMode TargetMode,
    string? SameItemId,
    List<MonitorTarget> PerMonitor,
    LiveScale Scale,
    double Volume,
    bool Muted,
    double Rate,
    LiveFrameCap FrameCap,
    bool PauseOnFullscreen,
    bool PauseOnBattery,
    bool PauseOnEnergySaver,
    bool PauseWhenCovered,
    bool PlaylistEnabled,
    int PlaylistMinutes,
    bool PlaylistShuffle,
    string? LastItemId,
    Dictionary<string, Dictionary<string, string>> PropertyValues)
{
    public static LiveSettings CreateDefault() => new(
        false, false, LiveTargetMode.SameOnAll, null, new List<MonitorTarget>(),
        LiveScale.Fill, 0.0, true, 1.0, LiveFrameCap.MatchDisplay,
        true, true, true, false,
        false, 15, false, null,
        new Dictionary<string, Dictionary<string, string>>());
}
