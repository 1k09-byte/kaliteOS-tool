using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Services.Personalize;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace kaliteConfig.ViewModels;

public sealed record MonitorChoice(string? MonitorId, string Label);

public sealed partial class PersonalizeViewModel : ObservableObject
{
    private readonly IThemeSettingsService _theme;
    private readonly ICursorService _cursor;
    private readonly IWallpaperService _wallpaper;
    private IReadOnlyDictionary<string, string>? _pendingScheme;

    public PersonalizeViewModel(
        IThemeSettingsService theme,
        ICursorService cursor,
        IWallpaperService wallpaper,
        WindowsCapabilityService capability)
    {
        _theme = theme;
        _cursor = cursor;
        _wallpaper = wallpaper;
        IsHighContrast = capability.IsHighContrastActive();
        Reload();
    }

    public ObservableCollection<WallpaperLibraryItem> Library { get; } = new();
    public ObservableCollection<MonitorChoice> Monitors { get; } = new();
    public ObservableCollection<WallpaperFit> Fits { get; } =
        new(Enum.GetValues<WallpaperFit>());

    [ObservableProperty] public partial WallpaperLibraryItem? SelectedWallpaper { get; set; }
    [ObservableProperty] public partial WallpaperFit SelectedFit { get; set; } = WallpaperFit.Fill;
    [ObservableProperty] public partial MonitorChoice? SelectedMonitor { get; set; }
    [ObservableProperty] public partial Color BackgroundColor { get; set; } = Color.FromArgb(255, 0, 0, 0);

    [ObservableProperty] public partial double PointerSize { get; set; } = 32;
    [ObservableProperty] public partial double PointerSpeed { get; set; } = 10;
    [ObservableProperty] public partial bool EnhancedPrecision { get; set; }
    [ObservableProperty] public partial double Trails { get; set; }
    [ObservableProperty] public partial double WheelLines { get; set; } = 3;
    [ObservableProperty] public partial double CaretWidth { get; set; } = 1;
    [ObservableProperty] public partial string? SchemeFolder { get; set; }
    [ObservableProperty] public partial int SchemeFileCount { get; set; }

    [ObservableProperty] public partial bool LibraryEmpty { get; set; }
    [ObservableProperty] public partial bool IsHighContrast { get; set; }
    [ObservableProperty] public partial bool StatusOpen { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    public void Reload()
    {
        var theme = _theme.Current();
        PointerSize = _cursor.CurrentPointerSize();
        var mouse = _cursor.CurrentMouse();
        PointerSpeed = mouse.PointerSpeed;
        EnhancedPrecision = mouse.EnhancedPrecision;
        Trails = mouse.Trails;
        WheelLines = mouse.WheelLines;
        CaretWidth = _cursor.CurrentCaretWidth();

        ReloadLibrary();

        Monitors.Clear();
        Monitors.Add(new MonitorChoice(null, "All monitors"));
        foreach (var m in _wallpaper.GetMonitors())
            Monitors.Add(new MonitorChoice(m.Id, $"Monitor {m.Index + 1} ({m.Width} x {m.Height})"));
        SelectedMonitor ??= Monitors[0];
    }

    private void ReloadLibrary()
    {
        var keep = SelectedWallpaper?.Id;
        Library.Clear();
        foreach (var item in _wallpaper.Library())
        {
            item.FullPath = _wallpaper.FilePathFor(item);
            Library.Add(item);
        }
        LibraryEmpty = Library.Count == 0;
        SelectedWallpaper = Library.FirstOrDefault(i => i.Id == keep);
    }

    public async Task AddWallpaperAsync(string sourcePath)
    {
        var (item, error) = await _wallpaper.AddToLibraryAsync(sourcePath);
        if (item is null)
        {
            Show(InfoBarSeverity.Warning, error ?? "The image could not be added.");
            return;
        }
        ReloadLibrary();
        SelectedWallpaper = Library.FirstOrDefault(i => i.Id == item.Id);
        Show(InfoBarSeverity.Success, "Added to the wallpaper library.");
    }

    [RelayCommand]
    private void ApplyWallpaper()
    {
        if (SelectedWallpaper is null)
        {
            Show(InfoBarSeverity.Warning, "Choose a wallpaper first.");
            return;
        }
        Report(_wallpaper.Apply(SelectedMonitor?.MonitorId, SelectedWallpaper, SelectedFit), "Wallpaper applied.");
    }

    [RelayCommand]
    private void ApplyBackgroundColor()
    {
        uint rgb = ((uint)BackgroundColor.R << 16) | ((uint)BackgroundColor.G << 8) | BackgroundColor.B;
        Report(_wallpaper.SetBackgroundColor(rgb), "Background color applied.");
    }

    [RelayCommand]
    private void RemoveSelectedWallpaper()
    {
        if (SelectedWallpaper is null) return;
        _wallpaper.RemoveFromLibrary(SelectedWallpaper.Id);
        ReloadLibrary();
    }

    [RelayCommand]
    private void RevertWallpaperSection() =>
        Report(_wallpaper.RevertSection(), "Wallpaper restored to how it was before this app changed it.");

    public IReadOnlyList<SettingResult> ApplyCursorOptions()
    {
        var results = new List<SettingResult>();
        results.AddRange(_cursor.ApplyPointerSize((int)Math.Round(PointerSize)));
        results.AddRange(_cursor.ApplyMouse(new MouseOptions(
            (int)Math.Round(PointerSpeed), EnhancedPrecision, (int)Math.Round(Trails), (int)Math.Round(WheelLines))));
        results.AddRange(_cursor.ApplyCaretWidth((int)Math.Round(CaretWidth)));
        Report(results, "Cursor settings applied.");
        return results;
    }

    public async Task ScanSchemeFolderAsync(string folder)
    {
        var scan = await Task.Run(() => _cursor.ScanSchemeFolder(folder));
        _pendingScheme = scan.Mapping;
        SchemeFolder = folder;
        SchemeFileCount = scan.Mapping.Count;
        if (scan.Mapping.Count == 0)
            Show(InfoBarSeverity.Warning, "No cursor files were found in that folder.");
        else if (scan.Skipped.Count > 0)
            Show(InfoBarSeverity.Warning, $"{scan.Skipped.Count} file(s) were skipped because they did not load as cursors.");
        else
            Show(InfoBarSeverity.Informational, $"Found {scan.Mapping.Count} cursor role(s). Apply to use them.");
    }

    public IReadOnlyList<SettingResult> ApplyCursorScheme()
    {
        if (_pendingScheme is null || _pendingScheme.Count == 0)
        {
            Show(InfoBarSeverity.Warning, "Choose a cursor scheme folder first.");
            return Array.Empty<SettingResult>();
        }
        var results = _cursor.ApplyScheme(_pendingScheme);
        Report(results, "Cursor scheme applied.");
        return results;
    }

    public void RevertCursorSection() =>
        Report(_cursor.RevertSection(), "Cursor restored to how it was before this app changed it.");

    [RelayCommand]
    private void ResetCursorToDefaults() =>
        Report(_cursor.ResetToWindowsDefaults(), "Cursor reset to Windows defaults.");

    [RelayCommand]
    private void RevertEverything()
    {
        var all = new List<SettingResult>();
        all.AddRange(_wallpaper.RevertSection());
        all.AddRange(_theme.RevertSection());
        all.AddRange(_cursor.RevertSection());
        Report(all, "Everything is back to how it was before this app changed it.");
    }

    private void Report(IEnumerable<SettingResult> results, string successText)
    {
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count == 0)
            Show(InfoBarSeverity.Success, successText);
        else
            Show(InfoBarSeverity.Warning, string.Join(" ", failed.Select(f => f.Message)));
        Reload();
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        StatusSeverity = severity;
        StatusMessage = message;
        StatusOpen = true;
    }
}
