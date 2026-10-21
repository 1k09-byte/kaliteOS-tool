using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
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

    public ObservableCollection<CursorPack> CursorPacks { get; } = new();

    [ObservableProperty] public partial CursorPack? SelectedCursorPack { get; set; }

    private void ReloadCursorPacks()
    {
        string? keep = SelectedCursorPack?.FolderPath;
        CursorPacks.Clear();
        foreach (var pack in _cursor.Packs())
            CursorPacks.Add(pack);
        SelectedCursorPack = CursorPacks.FirstOrDefault(p => p.FolderPath == keep)
            ?? CursorPacks.FirstOrDefault();
    }

    public async Task SelectCursorPackAsync(CursorPack? pack)
    {
        if (pack is null) return;
        var scan = await Task.Run(() => _cursor.ScanSchemeFolder(pack.FolderPath));
        if (scan.Mapping.Count == 0)
        {
            Show(InfoBarSeverity.Warning, $"No usable cursor files were found in '{pack.Name}'.");
            return;
        }
        _pendingScheme = scan.Mapping;
        SchemeFolder = pack.FolderPath;
        SchemeFileCount = scan.Mapping.Count;
        Report(_cursor.ApplyScheme(scan.Mapping), $"Cursor pack '{pack.Name}' applied.");
    }

    public async Task ImportCursorPackAsync(string folder)
    {
        var scan = await Task.Run(() => _cursor.ScanSchemeFolder(folder));
        if (scan.Mapping.Count == 0)
        {
            Show(InfoBarSeverity.Warning, "No cursor files were found in that folder.");
            return;
        }

        string root = Path.Combine(SchemeRootForPacks(), "cursor-packs");
        string name = new DirectoryInfo(folder).Name;
        string target = Path.Combine(root, name);
        try
        {
            Directory.CreateDirectory(root);
            if (!Directory.Exists(target)) CopyFolder(folder, target);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Warning, "Could not save that pack: " + ex.Message);
        }

        _pendingScheme = scan.Mapping;
        SchemeFolder = folder;
        SchemeFileCount = scan.Mapping.Count;
        ReloadCursorPacks();
        Report(_cursor.ApplyScheme(scan.Mapping), $"Cursor pack '{name}' applied.");
    }

    private string SchemeRootForPacks() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "personalize", "cursors");

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".cur" or ".ani")) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PointerSizeText))]
    public partial double PointerSize { get; set; } = 32;
    public string PointerSizeText => $"{Math.Round(PointerSize):0} px";
    public double PointerSizeMin => CursorService.MinPointerSize;
    public double PointerSizeMax => CursorService.MaxPointerSize;
    [ObservableProperty] public partial double PointerSpeed { get; set; } = 10;
    [ObservableProperty] public partial bool EnhancedPrecision { get; set; }
    [ObservableProperty] public partial double Trails { get; set; }
    [ObservableProperty] public partial double WheelLines { get; set; } = 3;
    [ObservableProperty] public partial double CaretWidth { get; set; } = 1;
    [ObservableProperty] public partial string? SchemeFolder { get; set; }
    [ObservableProperty] public partial int SchemeFileCount { get; set; }

    [ObservableProperty] public partial bool LibraryEmpty { get; set; }

    [ObservableProperty] public partial bool MatchAccentToWallpaper { get; set; } = true;

    [ObservableProperty] public partial Color AccentColor { get; set; } = Color.FromArgb(255, 0, 120, 212);

    [ObservableProperty] public partial bool AppsUseLightTheme { get; set; } = true;

    [ObservableProperty] public partial bool SystemUsesLightTheme { get; set; } = true;

    private bool _syncingTheme;

    [ObservableProperty] public partial string AccentPreviewText { get; set; } = "";

    [ObservableProperty] public partial int AccentPreviewArgb { get; set; } = unchecked((int)0xFF0078D4);
    [ObservableProperty] public partial bool IsHighContrast { get; set; }
    [ObservableProperty] public partial bool StatusOpen { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    public void Reload()
    {
        var theme = _theme.Current();
        _syncingTheme = true;
        AppsUseLightTheme = theme.AppsUseLight;
        SystemUsesLightTheme = theme.SystemUsesLight;
        _syncingTheme = false;
        PointerSize = _cursor.CurrentPointerSize();
        var mouse = _cursor.CurrentMouse();
        PointerSpeed = mouse.PointerSpeed;
        EnhancedPrecision = mouse.EnhancedPrecision;
        Trails = mouse.Trails;
        WheelLines = mouse.WheelLines;
        CaretWidth = _cursor.CurrentCaretWidth();

        try { ReloadLibrary(); } catch { }
        try { ReloadCursorPacks(); } catch { }

        Monitors.Clear();
        Monitors.Add(new MonitorChoice(null, "All monitors"));
        try
        {
            foreach (var m in _wallpaper.GetMonitors())
                Monitors.Add(new MonitorChoice(m.Id, $"Monitor {m.Index + 1} ({m.Width} x {m.Height})"));
        }
        catch { }
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
        var results = new List<SettingResult>();
        results.AddRange(_wallpaper.Apply(SelectedMonitor?.MonitorId, SelectedWallpaper, SelectedFit));
        if (MatchAccentToWallpaper) results.AddRange(ApplyAccentFrom(SelectedWallpaper));
        Report(results, "Wallpaper applied.");
    }

    private SettingResult[] ApplyAccentFrom(WallpaperLibraryItem item)
    {
        try
        {
            var extraction = AccentExtractor.Extract(item.FullPath);
            AccentPreviewArgb = extraction.Argb;
            AccentPreviewText = $"Accent #{extraction.Argb & 0xFFFFFF:X6}";

            var results = new List<SettingResult>(_theme.ApplyAccentColor(extraction.Argb));
            results.AddRange(_theme.Apply(new ThemeOptions(
                _theme.Current().AppsUseLight,
                extraction.Luminance < 0.35,
                _theme.Current().Transparency,
                _theme.Current().TitleBarAccent)));
            return results.ToArray();
        }
        catch (Exception ex)
        {
            return new[] { new SettingResult("theme.accent", false, "Could not read the wallpaper colours: " + ex.Message) };
        }
    }

    [RelayCommand]
    private void PreviewAccent()
    {
        if (SelectedWallpaper is null) return;
        var results = ApplyAccentFrom(SelectedWallpaper);
        Report(results, "Accent colours updated from the wallpaper.");
    }

    [RelayCommand]
    private void ApplyManualAccent()
    {
        int argb = unchecked((int)((((uint)AccentColor.A) << 24) | (((uint)AccentColor.R) << 16) | (((uint)AccentColor.G) << 8) | AccentColor.B));
        AccentPreviewArgb = argb;
        AccentPreviewText = $"Accent #{argb & 0xFFFFFF:X6}";
        Report(_theme.ApplyAccentColor(argb), "Accent colour applied.");
    }

    partial void OnAppsUseLightThemeChanged(bool value) => ApplyWindowsTheme();

    partial void OnSystemUsesLightThemeChanged(bool value) => ApplyWindowsTheme();

    private void ApplyWindowsTheme()
    {
        if (_syncingTheme) return;
        var current = _theme.Current();
        Report(_theme.Apply(new ThemeOptions(AppsUseLightTheme, SystemUsesLightTheme, current.Transparency, current.TitleBarAccent)), "Windows theme updated.");
    }

    [RelayCommand]
    private void RemoveSelectedCursorPack()
    {
        if (SelectedCursorPack is null)
        {
            Show(InfoBarSeverity.Warning, "Select a cursor pack first.");
            return;
        }
        if (SelectedCursorPack.IsBundled)
        {
            Show(InfoBarSeverity.Warning, "Bundled packs cannot be removed.");
            return;
        }
        try
        {
            string root = SchemeRootForPacks();
            string full = Path.GetFullPath(SelectedCursorPack.FolderPath);
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
            {
                Show(InfoBarSeverity.Warning, "That pack lives outside the app library and cannot be removed here.");
                return;
            }
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Warning, "Could not remove that pack: " + ex.Message);
            return;
        }
        ReloadCursorPacks();
        Show(InfoBarSeverity.Success, "Cursor pack removed.");
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

    [RelayCommand]
    private void ApplyPointerSize() =>
        Report(_cursor.ApplyPointerSize((int)Math.Round(PointerSize)), "Pointer size applied.");

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
