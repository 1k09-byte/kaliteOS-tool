using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Services.LiveWallpaper;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace kaliteConfig.ViewModels;

public sealed record LiveMonitorChoice(string? Id, string Label);

public sealed partial class LiveAssignmentRow : ObservableObject
{
    public LiveAssignmentRow(string monitorId, string monitorLabel)
    {
        MonitorId = monitorId;
        MonitorLabel = monitorLabel;
    }

    public string MonitorId { get; }
    public string MonitorLabel { get; }

    [ObservableProperty]
    public partial LiveWallpaperItem? SelectedItem { get; set; }

    internal Action? Changed;

    partial void OnSelectedItemChanged(LiveWallpaperItem? value) => Changed?.Invoke();
}

public sealed partial class LivePropertyRow : ObservableObject
{
    public LivePropertyRow(string folder, LivePropertyDef def, string saved)
    {
        Folder = folder;
        Def = def;
        BoolValue = saved == "true" || saved == "1";
        if (double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            NumberValue = Math.Max(def.Min, Math.Min(def.Max, n));
            TextValue = saved;
        }
        else
        {
            NumberValue = def.Min;
            TextValue = string.IsNullOrEmpty(saved) ? def.Default : saved;
        }
        ColorValue = ParseColor(saved, def.Default);
    }

    public string Folder { get; }
    public LivePropertyDef Def { get; }

    public bool IsBoolean => Def.Type == "boolean";
    public bool IsNumber => Def.Type == "number";
    public bool IsChoice => Def.Type == "choice";
    public bool IsColor => Def.Type == "color";

    [ObservableProperty]
    public partial bool BoolValue { get; set; }

    [ObservableProperty]
    public partial double NumberValue { get; set; }

    [ObservableProperty]
    public partial string TextValue { get; set; }

    [ObservableProperty]
    public partial Color ColorValue { get; set; }

    internal Action<LivePropertyRow>? Changed;

    partial void OnBoolValueChanged(bool value) => Changed?.Invoke(this);
    partial void OnNumberValueChanged(double value) => Changed?.Invoke(this);
    partial void OnTextValueChanged(string value) => Changed?.Invoke(this);
    partial void OnColorValueChanged(Color value) => Changed?.Invoke(this);

    public string Serialize() => Def.Type switch
    {
        "boolean" => BoolValue ? "true" : "false",
        "number" => NumberValue.ToString(CultureInfo.InvariantCulture),
        "color" => $"#{ColorValue.R:X2}{ColorValue.G:X2}{ColorValue.B:X2}",
        _ => TextValue,
    };

    private static Color ParseColor(string saved, string fallback)
    {
        foreach (var text in new[] { saved, fallback })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            string hex = text.TrimStart('#');
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return Color.FromArgb(255, 0, 120, 212);
    }
}

public sealed partial class LiveWallpaperViewModel : ObservableObject
{
    private static readonly double[] Rates = { 0.25, 0.5, 1.0, 1.5, 2.0 };
    private static readonly int[] Minutes = { 1, 5, 15, 30, 60 };

    private readonly ILiveWallpaperEngine _engine;
    private readonly IDesktopHostService _hosts;
    private readonly ILiveLibraryService _library;
    private bool _syncing;

    public LiveWallpaperViewModel(
        ILiveWallpaperEngine engine,
        IDesktopHostService hosts,
        ILiveLibraryService library)
    {
        _engine = engine;
        _hosts = hosts;
        _library = library;
        _engine.Changed += RefreshState;
        LoadSettingsInto();
        RefreshMonitors();
        RefreshState();
    }

    public ObservableCollection<LiveWallpaperItem> Items { get; } = new();
    public ObservableCollection<LiveMonitorChoice> Monitors { get; } = new();
    public ObservableCollection<LiveAssignmentRow> Assignments { get; } = new();
    public ObservableCollection<LivePropertyRow> Props { get; } = new();

    public string[] TargetLabels { get; } = { "Same on all monitors", "Different per monitor", "Span all monitors" };
    public string[] RateLabels { get; } = { "0.25×", "0.5×", "Normal", "1.5×", "2×" };
    public string[] FrameCapLabels { get; } = { "Match display", "60 fps", "30 fps", "15 fps" };
    public string[] MinutesLabels { get; } = { "Every minute", "Every 5 minutes", "Every 15 minutes", "Every 30 minutes", "Every hour" };

    [ObservableProperty] public partial LiveWallpaperItem? SelectedItem { get; set; }
    [ObservableProperty] public partial LiveWallpaperItem? SameItem { get; set; }
    [ObservableProperty] public partial LiveMonitorChoice? SelectedMonitorChoice { get; set; }
    [ObservableProperty] public partial bool FeatureEnabled { get; set; }
    [ObservableProperty] public partial bool ResumeOnLaunch { get; set; }
    [ObservableProperty] public partial int TargetModeIndex { get; set; }
    [ObservableProperty] public partial int ScaleIndex { get; set; }
    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool Muted { get; set; } = true;
    [ObservableProperty] public partial int RateIndex { get; set; } = 2;
    [ObservableProperty] public partial int FrameCapIndex { get; set; }
    [ObservableProperty] public partial bool PauseOnFullscreen { get; set; } = true;
    [ObservableProperty] public partial bool PauseOnBattery { get; set; } = true;
    [ObservableProperty] public partial bool PauseOnEnergySaver { get; set; } = true;
    [ObservableProperty] public partial bool PauseWhenCovered { get; set; }
    [ObservableProperty] public partial bool PlaylistEnabled { get; set; }
    [ObservableProperty] public partial int PlaylistMinutesIndex { get; set; } = 2;
    [ObservableProperty] public partial bool PlaylistShuffle { get; set; }
    [ObservableProperty] public partial bool IsStarting { get; set; }
    [ObservableProperty] public partial bool LibraryEmpty { get; set; } = true;
    [ObservableProperty] public partial string StatusText { get; set; } = "Stopped.";
    [ObservableProperty] public partial string GateReason { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseEnabled))]
    public partial bool IsPlaying { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseEnabled))]
    public partial bool IsPaused { get; set; }

    public bool PauseEnabled => IsPlaying || IsPaused;
    [ObservableProperty] public partial string PauseButtonText { get; set; } = "Pause";
    [ObservableProperty] public partial bool SameOrSpanVisible { get; set; } = true;
    [ObservableProperty] public partial bool PerMonitorVisible { get; set; }
    [ObservableProperty] public partial bool HasManifestProps { get; set; }

    partial void OnSelectedItemChanged(LiveWallpaperItem? value) => RefreshProps();

    partial void OnFeatureEnabledChanged(bool value) => PushSettings();
    partial void OnResumeOnLaunchChanged(bool value) => PushSettings();
    partial void OnTargetModeIndexChanged(int value) => ModeChanged();
    partial void OnScaleIndexChanged(int value) => PushSettings();
    partial void OnVolumeChanged(double value) => PushSettings();
    partial void OnMutedChanged(bool value) => PushSettings();
    partial void OnRateIndexChanged(int value) => PushSettings();
    partial void OnFrameCapIndexChanged(int value) => PushSettings();
    partial void OnPauseOnFullscreenChanged(bool value) => PushSettings();
    partial void OnPauseOnBatteryChanged(bool value) => PushSettings();
    partial void OnPauseOnEnergySaverChanged(bool value) => PushSettings();
    partial void OnPauseWhenCoveredChanged(bool value) => PushSettings();
    partial void OnPlaylistEnabledChanged(bool value) => PushSettings();
    partial void OnPlaylistMinutesIndexChanged(int value) => PushSettings();
    partial void OnPlaylistShuffleChanged(bool value) => PushSettings();

    public async Task ReloadLibraryAsync()
    {
        var items = await Task.Run(() => _library.List()).ConfigureAwait(true);
        Items.Clear();
        foreach (var item in items)
        {
            try { item.Thumb = await LoadThumbAsync(item).ConfigureAwait(true); } catch { }
            Items.Add(item);
        }
        LibraryEmpty = Items.Count == 0;
        if (SelectedItem is null || !Items.Any(i => i.Id == SelectedItem.Id))
            SelectedItem = Items.FirstOrDefault();
        if (SameItem is null || !Items.Any(i => i.Id == SameItem.Id))
            SameItem = SelectedItem;
        RebuildAssignments();
        RefreshProps();
    }

    public async Task AddVideoPathAsync(string path)
    {
        var (item, error) = await _library.AddVideoFileAsync(path).ConfigureAwait(true);
        if (item is null)
        {
            StatusText = error ?? "That video could not be added.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }
        await ReloadLibraryAsync().ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
        StatusText = "Added to the live library.";
        StatusSeverity = InfoBarSeverity.Success;
    }

    public async Task AddFolderPathAsync(string path)
    {
        var (item, error) = await _library.AddHtmlFolderAsync(path).ConfigureAwait(true);
        if (item is null)
        {
            StatusText = error ?? "That folder could not be added.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }
        await ReloadLibraryAsync().ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
        StatusText = "Added to the live library.";
        StatusSeverity = InfoBarSeverity.Success;
    }

    public async Task AddPackagePathAsync(string path)
    {
        var (item, error) = await _library.AddPackageAsync(path).ConfigureAwait(true);
        if (item is null)
        {
            StatusText = error ?? "That package could not be added.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }
        await ReloadLibraryAsync().ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(i => i.Id == item.Id);
        StatusText = "Added to the live library.";
        StatusSeverity = InfoBarSeverity.Success;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedItem is null) return;
        _library.Remove(SelectedItem.Id);
        _ = ReloadLibraryAsync();
    }

    public bool RenameSelected(string name)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(name)) return false;
        if (!_library.Rename(SelectedItem.Id, name)) return false;
        _ = ReloadLibraryAsync();
        return true;
    }

    [RelayCommand]
    private void SetSelectedOnMonitor()
    {
        if (SelectedItem is null || SelectedMonitorChoice?.Id is null) return;
        var row = Assignments.FirstOrDefault(a => a.MonitorId == SelectedMonitorChoice.Id);
        if (row is null) return;
        row.SelectedItem = SelectedItem;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsStarting) return;
        var targets = BuildTargets();
        if (targets.Count == 0)
        {
            StatusText = "Pick a wallpaper first.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }
        IsStarting = true;
        string? startError = null;
        try
        {
            var (ok, error) = await _engine.StartAsync(targets).ConfigureAwait(true);
            if (!ok) startError = error ?? "The wallpaper could not start.";
        }
        finally
        {
            IsStarting = false;
        }
        RefreshState();
        if (startError is not null)
        {
            StatusText = startError;
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void Stop() => _engine.StopAndRestore();

    [RelayCommand]
    private void TogglePause()
    {
        if (_engine.State == LiveState.Playing) _engine.Pause();
        else if (_engine.State == LiveState.Paused) _engine.Resume();
    }

    [RelayCommand]
    private void Next() => _engine.PlaylistNext();

    public async Task ResumeOnLaunchAsync()
    {
        try { await _engine.TryResumeOnLaunchAsync().ConfigureAwait(true); } catch { }
        RefreshState();
    }

    private List<(MonitorRect Area, LiveWallpaperItem Item)> BuildTargets()
    {
        var areas = _hosts.GetMonitors();
        var targets = new List<(MonitorRect, LiveWallpaperItem)>();
        if (TargetModeIndex == 1)
        {
            foreach (var row in Assignments)
            {
                if (row.SelectedItem is null) continue;
                var area = areas.FirstOrDefault(a => a.Id == row.MonitorId);
                if (area is not null) targets.Add((area, row.SelectedItem));
            }
            return targets;
        }
        var picked = SameItem ?? SelectedItem;
        if (picked is null) return targets;
        if (TargetModeIndex == 2 && areas.Count > 0)
        {
            int left = areas.Min(a => a.X);
            int top = areas.Min(a => a.Y);
            int right = areas.Max(a => a.X + a.Width);
            int bottom = areas.Max(a => a.Y + a.Height);
            targets.Add((new MonitorRect("span", left, top, right - left, bottom - top), picked));
            return targets;
        }
        foreach (var area in areas) targets.Add((area, picked));
        return targets;
    }

    private void RefreshMonitors()
    {
        Monitors.Clear();
        foreach (var area in _hosts.GetMonitors())
            Monitors.Add(new LiveMonitorChoice(area.Id, $"Monitor ({area.Width} x {area.Height})"));
        SelectedMonitorChoice ??= Monitors.FirstOrDefault();
        RebuildAssignments();
    }

    private void RebuildAssignments()
    {
        var keep = Assignments.ToDictionary(a => a.MonitorId, a => a.SelectedItem);
        Assignments.Clear();
        foreach (var monitor in Monitors)
        {
            if (monitor.Id is null) continue;
            var row = new LiveAssignmentRow(monitor.Id, monitor.Label);
            row.Changed += PushSettings;
            if (keep.TryGetValue(monitor.Id, out var item) && item is not null && Items.Any(i => i.Id == item.Id))
                row.SelectedItem = Items.First(i => i.Id == item.Id);
            else
                row.SelectedItem = SameItem ?? SelectedItem;
            Assignments.Add(row);
        }
    }

    private void ModeChanged()
    {
        SameOrSpanVisible = TargetModeIndex != 1;
        PerMonitorVisible = TargetModeIndex == 1;
        PushSettings();
    }

    private void LoadSettingsInto()
    {
        _syncing = true;
        try
        {
            var s = _engine.CurrentSettings;
            FeatureEnabled = s.FeatureEnabled;
            ResumeOnLaunch = s.ResumeOnLaunch;
            TargetModeIndex = (int)s.TargetMode;
            ScaleIndex = (int)s.Scale;
            Volume = s.Volume;
            Muted = s.Muted;
            RateIndex = Array.IndexOf(Rates, s.Rate);
            if (RateIndex < 0) RateIndex = 2;
            FrameCapIndex = (int)s.FrameCap;
            PauseOnFullscreen = s.PauseOnFullscreen;
            PauseOnBattery = s.PauseOnBattery;
            PauseOnEnergySaver = s.PauseOnEnergySaver;
            PauseWhenCovered = s.PauseWhenCovered;
            PlaylistEnabled = s.PlaylistEnabled;
            PlaylistMinutesIndex = Array.IndexOf(Minutes, s.PlaylistMinutes);
            if (PlaylistMinutesIndex < 0) PlaylistMinutesIndex = 2;
            PlaylistShuffle = s.PlaylistShuffle;
            SameOrSpanVisible = TargetModeIndex != 1;
            PerMonitorVisible = TargetModeIndex == 1;
        }
        finally { _syncing = false; }
    }

    private LiveSettings ToSettings()
    {
        var perMonitor = Assignments
            .Where(a => a.SelectedItem is not null)
            .Select(a => new MonitorTarget(a.MonitorId, a.SelectedItem!.Id))
            .ToList();
        return new LiveSettings(
            FeatureEnabled, ResumeOnLaunch, (LiveTargetMode)TargetModeIndex,
            SameItem?.Id, perMonitor,
            (LiveScale)ScaleIndex, Volume, Muted,
            Rates[Math.Max(0, Math.Min(RateIndex, Rates.Length - 1))],
            (LiveFrameCap)FrameCapIndex,
            PauseOnFullscreen, PauseOnBattery, PauseOnEnergySaver, PauseWhenCovered,
            PlaylistEnabled, Minutes[Math.Max(0, Math.Min(PlaylistMinutesIndex, Minutes.Length - 1))],
            PlaylistShuffle, _engine.CurrentSettings.LastItemId,
            _engine.CurrentSettings.PropertyValues);
    }

    private void PushSettings()
    {
        if (_syncing) return;
        try { _engine.ApplySettings(ToSettings()); } catch { }
        RefreshState();
    }

    private void RefreshState()
    {
        StatusText = _engine.StatusDetail;
        IsPlaying = _engine.State == LiveState.Playing;
        IsPaused = _engine.State == LiveState.Paused;
        PauseButtonText = IsPaused ? "Resume" : "Pause";
        StatusSeverity = _engine.State switch
        {
            LiveState.Playing => InfoBarSeverity.Success,
            LiveState.Paused => InfoBarSeverity.Warning,
            LiveState.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        if (!_engine.CanStart(out string reason)) GateReason = reason;
        else GateReason = string.Empty;
    }

    private void RefreshProps()
    {
        foreach (var row in Props) row.Changed -= OnPropChanged;
        Props.Clear();
        HasManifestProps = false;
        try
        {
            if (SelectedItem is null || SelectedItem.Kind != LiveKind.Html) return;
            string manifestPath = Path.Combine(SelectedItem.Path, "manifest.json");
            if (!File.Exists(manifestPath)) return;
            var service = new WallpaperPackageService();
            var (manifest, _) = service.TryParseManifest(File.ReadAllText(manifestPath), SelectedItem.Path);
            if (manifest?.Properties is null) return;
            _engine.CurrentSettings.PropertyValues.TryGetValue(SelectedItem.Path, out var saved);
            saved ??= new Dictionary<string, string>();
            foreach (var pair in manifest.Properties)
            {
                saved.TryGetValue(pair.Key, out var value);
                var row = new LivePropertyRow(SelectedItem.Path, pair.Value, value ?? string.Empty);
                row.Changed += OnPropChanged;
                Props.Add(row);
            }
            HasManifestProps = Props.Count > 0;
        }
        catch { }
    }

    private void OnPropChanged(LivePropertyRow row)
    {
        try
        {
            var settings = ToSettings();
            if (!settings.PropertyValues.TryGetValue(row.Folder, out var values))
            {
                values = new Dictionary<string, string>();
                settings.PropertyValues[row.Folder] = values;
            }
            values[row.Def.Key] = row.Serialize();
            _engine.ApplySettings(settings);
        }
        catch { }
    }

    private async Task<Microsoft.UI.Xaml.Media.ImageSource?> LoadThumbAsync(LiveWallpaperItem item)
    {
        try
        {
            if (item.Kind == LiveKind.Video)
            {
                using var stream = await _library.ReadVideoThumbnailAsync(item.Path).ConfigureAwait(true);
                if (stream is null || stream.Size == 0) return null;
                var image = new BitmapImage();
                image.SetSource(stream);
                return image;
            }
            if (!string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath))
                return new BitmapImage(new Uri(item.PreviewPath)) { DecodePixelWidth = 320 };
            string entry = Path.Combine(item.Path, "preview.png");
            if (File.Exists(entry))
                return new BitmapImage(new Uri(entry)) { DecodePixelWidth = 320 };
        }
        catch { }
        return null;
    }
}
