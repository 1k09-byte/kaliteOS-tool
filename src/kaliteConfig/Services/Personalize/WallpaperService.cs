using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace kaliteConfig.Services.Personalize;

public enum WallpaperFit { Center, Tile, Stretch, Fit, Fill, Span }

public sealed record MonitorInfo(string Id, int Index, int Width, int Height);

public sealed class WallpaperLibraryItem
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public DateTime AddedUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FullPath { get; set; } = "";
}

public sealed class WallpaperLibraryState
{
    public List<WallpaperLibraryItem> Items { get; set; } = new();
    public List<string> RecentIds { get; set; } = new();
}

public interface IWallpaperService
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    IReadOnlyList<WallpaperLibraryItem> Library();
    IReadOnlyList<WallpaperLibraryItem> Recent();
    string FilePathFor(WallpaperLibraryItem item);
    Task<(WallpaperLibraryItem? Item, string? Error)> AddToLibraryAsync(string sourcePath);
    bool RemoveFromLibrary(string id);
    IReadOnlyList<SettingResult> Apply(string? monitorId, WallpaperLibraryItem item, WallpaperFit fit);
    IReadOnlyList<SettingResult> SetBackgroundColor(uint rgb);
    IReadOnlyList<SettingResult> StartSlideshow(string folder, int intervalMinutes, bool shuffle);
    IReadOnlyList<SettingResult> NextWallpaper();
    IReadOnlyList<SettingResult> RevertSection();
}

public sealed class WallpaperService : IWallpaperService
{
    public const string DesktopKey = @"Control Panel\Desktop";
    public const string ColorsKey = @"Control Panel\Colors";
    private const int RecentLimit = 8;
    private const long MaxImageBytes = 50L * 1024 * 1024;
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    private readonly SnapshotStore _snapshots;
    private readonly SettingApplier _applier;
    private readonly string _root;
    private readonly string _stateFile;

    public WallpaperService(SnapshotStore snapshots, SettingApplier applier, string? root = null)
    {
        _snapshots = snapshots;
        _applier = applier;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "personalize", "wallpapers");
        _stateFile = Path.Combine(_root, "library.json");
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            using var api = new DesktopWallpaperApi();
            return api.Monitors()
                .Select((m, i) => new MonitorInfo(m.Id, i, m.Rect.Right - m.Rect.Left, m.Rect.Bottom - m.Rect.Top))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<MonitorInfo>();
        }
    }

    public IReadOnlyList<WallpaperLibraryItem> Library()
    {
        SeedBundled();
        return LoadState().Items.OrderBy(i => i.AddedUtc)
        .Select(i => { i.FullPath = FilePathFor(i); return i; }).ToList();
    }

    private static string? BundledWallpaperDir
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 4 && dir is not null; i++)
            {
                string candidate = Path.Combine(dir, "Assets", "wallpapers");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return null;
        }
    }

    private bool _seeded;

    private void SeedBundled()
    {
        if (_seeded) return;
        _seeded = true;

        try
        {
            string? source = BundledWallpaperDir;
            if (source is null) return;

            var state = LoadState();
            var known = new HashSet<string>(state.Items.Select(i => i.OriginalName), StringComparer.OrdinalIgnoreCase);
            bool added = false;

            foreach (var file in Directory.EnumerateFiles(source).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("kalite-config.png", StringComparison.OrdinalIgnoreCase)) continue;
                
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext)) continue;

                if (!known.Add(name)) continue;

                Directory.CreateDirectory(_root);
                string id = Guid.NewGuid().ToString("N");
                string dest = Path.Combine(_root, id + ext);
                try { File.Copy(file, dest); }
                catch { continue; }

                state.Items.Add(new WallpaperLibraryItem
                {
                    Id = id,
                    FileName = id + ext,
                    OriginalName = name,
                    AddedUtc = DateTime.UtcNow,
                });
                added = true;
            }

            if (added) SaveState(state);
        }
        catch
        {
        }
    }

    public IReadOnlyList<WallpaperLibraryItem> Recent()
    {
        var state = LoadState();
        return state.RecentIds
            .Select(id => state.Items.FirstOrDefault(i => i.Id == id))
            .Where(i => i is not null)
            .Cast<WallpaperLibraryItem>()
            .ToList();
    }

    public string FilePathFor(WallpaperLibraryItem item) => Path.Combine(_root, item.FileName);

    public async Task<(WallpaperLibraryItem? Item, string? Error)> AddToLibraryAsync(string sourcePath)
    {
        string full;
        try { full = ValidateSourceImage(sourcePath); }
        catch (Exception ex) { return (null, ex.Message); }

        string ext = Path.GetExtension(full).ToLowerInvariant();
        string id = Guid.NewGuid().ToString("N");
        string dest = Path.Combine(_root, id + ext);
        Directory.CreateDirectory(_root);
        try
        {
            using (var check = File.OpenRead(full))
            using (var ras = check.AsRandomAccessStream())
            {
                await BitmapDecoder.CreateAsync(ras).AsTask();
            }
            File.Copy(full, dest, overwrite: false);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            return (null, "This file is not a valid image: " + ex.Message);
        }

        var item = new WallpaperLibraryItem
        {
            Id = id,
            FileName = id + ext,
            OriginalName = Path.GetFileName(full),
            AddedUtc = DateTime.UtcNow,
        };
        var state = LoadState();
        state.Items.Add(item);
        SaveState(state);
        return (item, null);
    }

    public bool RemoveFromLibrary(string id)
    {
        var state = LoadState();
        var item = state.Items.FirstOrDefault(i => i.Id == id);
        if (item is null) return false;
        state.Items.Remove(item);
        state.RecentIds.Remove(id);
        SaveState(state);
        try { File.Delete(FilePathFor(item)); } catch {  }
        return true;
    }

    public IReadOnlyList<SettingResult> Apply(string? monitorId, WallpaperLibraryItem item, WallpaperFit fit)
    {
        var results = new List<SettingResult>();
        string path = FilePathFor(item);
        if (!File.Exists(path)) return Fail("wallpaper.apply", "The wallpaper file is missing from the library.");

        CaptureRegistrySnapshots();
        using var api = new DesktopWallpaperApi();
        var targets = monitorId is null
            ? api.Monitors().Select(m => m.Id).ToList()
            : new List<string> { monitorId };

        int position = MapFit(fit);
        foreach (var target in targets)
        {
            string id = "wallpaper.monitor." + target;
            string? previous = null;
            int previousPos = 0;
            try
            {
                previous = api.GetWallpaper(target);
                previousPos = api.GetPosition();
                api.SetPosition(position);
                api.SetWallpaper(target, path);

                string? now = api.GetWallpaper(target);
                bool ok = string.Equals(now, path, StringComparison.OrdinalIgnoreCase)
                          && api.GetPosition() == position;
                if (ok)
                {
                    results.Add(new SettingResult(id, true, "Applied."));
                    continue;
                }
                RestorePrevious(api, target, previous, previousPos);
                results.Add(new SettingResult(id, false,
                    "Windows did not keep the new wallpaper. It may be locked by policy. The previous one was restored."));
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                try { RestorePrevious(api, target, previous, previousPos); }
                catch (Exception rex) { message += " Rollback failed: " + rex.Message; }
                results.Add(new SettingResult(id, false, message));
            }
        }

        if (results.Any(r => r.Ok)) RecordRecent(item.Id);
        return results;
    }

    public IReadOnlyList<SettingResult> SetBackgroundColor(uint rgb)
    {
        CaptureRegistrySnapshots();
        using var api = new DesktopWallpaperApi();
        uint colorref = ToColorRef(rgb);
        uint previous = api.GetBackgroundColor();
        try
        {
            api.SetBackgroundColor(colorref);
            if (api.GetBackgroundColor() == colorref) return Ok("wallpaper.bgcolor", "Applied.");
            api.SetBackgroundColor(previous);
            return Fail("wallpaper.bgcolor", "Windows did not keep the background color. The previous color was restored.");
        }
        catch (Exception ex)
        {
            try { api.SetBackgroundColor(previous); } catch { }
            return Fail("wallpaper.bgcolor", ex.Message);
        }
    }

    public IReadOnlyList<SettingResult> StartSlideshow(string folder, int intervalMinutes, bool shuffle)
    {
        string full;
        try { full = ValidateFolder(folder); }
        catch (Exception ex) { return Fail("wallpaper.slideshow", ex.Message); }
        if (intervalMinutes < 1 || intervalMinutes > 1440)
            return Fail("wallpaper.slideshow", "The interval must be between 1 and 1440 minutes.");

        CaptureRegistrySnapshots();
        try
        {
            using var api = new DesktopWallpaperApi();
            api.SetSlideshowFolder(full);
            api.SetSlideshowOptions(shuffle, (uint)intervalMinutes * 60_000u);
            return Ok("wallpaper.slideshow", "Slideshow started.");
        }
        catch (Exception ex)
        {
            return Fail("wallpaper.slideshow", ex.Message);
        }
    }

    public IReadOnlyList<SettingResult> NextWallpaper()
    {
        try
        {
            using var api = new DesktopWallpaperApi();
            var first = api.Monitors().FirstOrDefault();
            if (first.Id is null) return Fail("wallpaper.next", "No monitor found.");
            api.AdvanceSlideshow(first.Id);
            return Ok("wallpaper.next", "Moved to the next wallpaper.");
        }
        catch (Exception ex)
        {
            return Fail("wallpaper.next", ex.Message);
        }
    }

    public IReadOnlyList<SettingResult> RevertSection()
    {
        var results = new List<SettingResult>(_applier.RevertWhere(
            e => e.Id.StartsWith("wallpaper.", StringComparison.Ordinal),
            new (string, Func<bool>)[]
            {
                ("wallpaper", SyncWallpaperFromRegistry),
                ("bgcolor", SyncBackgroundFromRegistry),
            }));
        return results;
    }

    private void CaptureRegistrySnapshots()
    {
        _snapshots.CaptureIfNeeded("wallpaper.path", DesktopKey, "Wallpaper");
        _snapshots.CaptureIfNeeded("wallpaper.style", DesktopKey, "WallpaperStyle");
        _snapshots.CaptureIfNeeded("wallpaper.tile", DesktopKey, "TileWallpaper");
        _snapshots.CaptureIfNeeded("wallpaper.bgcolor", ColorsKey, "Background");
    }

    private static void RestorePrevious(DesktopWallpaperApi api, string target, string? previous, int previousPos)
    {
        api.SetPosition(previousPos);
        if (!string.IsNullOrEmpty(previous)) api.SetWallpaper(target, previous);
    }

    internal static int MapFit(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Center => DesktopWallpaperPosition.Center,
        WallpaperFit.Tile => DesktopWallpaperPosition.Tile,
        WallpaperFit.Stretch => DesktopWallpaperPosition.Stretch,
        WallpaperFit.Fit => DesktopWallpaperPosition.Fit,
        WallpaperFit.Fill => DesktopWallpaperPosition.Fill,
        WallpaperFit.Span => DesktopWallpaperPosition.Span,
        _ => throw new ArgumentOutOfRangeException(nameof(fit)),
    };

    internal static uint ToColorRef(uint rgb)
    {
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return (b << 16) | (g << 8) | r;
    }

    private bool SyncWallpaperFromRegistry()
    {
        var entry = HkcuRegistry.ReadEntry(DesktopKey, "Wallpaper");
        string path = entry.Exists ? Convert.ToString(entry.Data, CultureInfo.InvariantCulture) ?? "" : "";
        return NativeMethods.SystemParametersInfoString(
            NativeMethods.SPI_SETDESKWALLPAPER, 0, path,
            NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
    }

    private static bool SyncBackgroundFromRegistry()
    {
        var entry = HkcuRegistry.ReadEntry(ColorsKey, "Background");
        var parts = (Convert.ToString(entry.Data, CultureInfo.InvariantCulture) ?? "0 0 0")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;
        int r = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int g = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int b = int.Parse(parts[2], CultureInfo.InvariantCulture);
        return NativeMethods.SetSysColors(1, new[] { NativeMethods.COLOR_BACKGROUND }, new[] { (uint)(r | (g << 8) | (b << 16)) });
    }

    private void RecordRecent(string id)
    {
        var state = LoadState();
        state.RecentIds.Remove(id);
        state.RecentIds.Insert(0, id);
        if (state.RecentIds.Count > RecentLimit) state.RecentIds.RemoveRange(RecentLimit, state.RecentIds.Count - RecentLimit);
        SaveState(state);
    }

    internal static string ValidateSourceImage(string path)
    {
        string full = ValidateLocalPath(path);
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException("Only JPG, PNG, BMP and WebP images can be used.");
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("The file does not exist.", full);
        if (info.Length == 0 || info.Length > MaxImageBytes)
            throw new InvalidOperationException("The image must be between 1 byte and 50 MB.");
        return full;
    }

    internal static string ValidateFolder(string path)
    {
        string full = ValidateLocalPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The folder does not exist.");
        return full;
    }

    internal static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file was given.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"//", StringComparison.Ordinal))
            throw new InvalidOperationException("Network and UNC paths are not allowed.");
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException("Network and UNC paths are not allowed.");
        return full;
    }

    private WallpaperLibraryState LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return new WallpaperLibraryState();
            return JsonSerializer.Deserialize<WallpaperLibraryState>(File.ReadAllText(_stateFile)) ?? new WallpaperLibraryState();
        }
        catch (JsonException)
        {
            try { File.Move(_stateFile, _stateFile + ".corrupt", true); } catch { }
            return new WallpaperLibraryState();
        }
    }

    private void SaveState(WallpaperLibraryState state)
    {
        Directory.CreateDirectory(_root);
        var tmp = _stateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _stateFile, overwrite: true);
    }

    private static IReadOnlyList<SettingResult> Ok(string id, string message) => new[] { new SettingResult(id, true, message) };
    private static IReadOnlyList<SettingResult> Fail(string id, string message) => new[] { new SettingResult(id, false, message) };
}
