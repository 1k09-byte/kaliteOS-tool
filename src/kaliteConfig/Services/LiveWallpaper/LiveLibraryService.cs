using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace kaliteConfig.Services.LiveWallpaper;

public interface ILiveLibraryService
{
    List<LiveWallpaperItem> List();
    Task<(LiveWallpaperItem? Item, string? Error)> AddVideoFileAsync(string sourcePath);
    Task<(LiveWallpaperItem? Item, string? Error)> AddHtmlFolderAsync(string sourceFolder);
    Task<(LiveWallpaperItem? Item, string? Error)> AddPackageAsync(string zipPath);
    bool Rename(string id, string name);
    bool Remove(string id);
}

public sealed class LiveLibraryService : ILiveLibraryService
{
    private readonly LiveWallpaperStore _store = new();
    private readonly WallpaperPackageService _packages = new();

    public List<LiveWallpaperItem> List() =>
        _store.LoadLibrary().OrderByDescending(i => i.AddedUtc).ToList();

    public async Task<(LiveWallpaperItem? Item, string? Error)> AddVideoFileAsync(string sourcePath)
    {
        if (!WallpaperPackageService.IsLocalPath(sourcePath) || !File.Exists(sourcePath))
            return (null, "That file could not be found.");
        if (!WallpaperPackageService.IsVideoFile(sourcePath))
            return (null, "Only mp4, m4v, mov, wmv, webm and mkv files can be used.");
        try
        {
            if (new FileInfo(sourcePath).Length > (4L << 30))
                return (null, "That video is larger than 4 GB.");
        }
        catch { return (null, "That file could not be read."); }
        TimeSpan? duration = await ReadVideoDurationAsync(sourcePath).ConfigureAwait(false);
        if (duration is null || duration.Value <= TimeSpan.Zero)
            return (null, "That video could not be decoded here.");
        try
        {
            string destDir = Path.Combine(LiveWallpaperStore.FilesDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destDir);
            string dest = Path.Combine(destDir, Path.GetFileName(sourcePath));
            await Task.Run(() => File.Copy(sourcePath, dest, overwrite: true)).ConfigureAwait(false);
            var item = new LiveWallpaperItem(Guid.NewGuid().ToString("N"), LiveKind.Video,
                Path.GetFileNameWithoutExtension(sourcePath), dest, null, DateTime.UtcNow);
            var items = _store.LoadLibrary();
            items.Add(item);
            _store.SaveLibrary(items);
            return (item, null);
        }
        catch (Exception ex)
        {
            return (null, "That video could not be added: " + ex.Message);
        }
    }

    public async Task<(LiveWallpaperItem? Item, string? Error)> AddHtmlFolderAsync(string sourceFolder)
    {
        string? folder = null;
        LiveManifest? manifest = null;
        string? manifestError = null;
        await Task.Run(() =>
        {
            string rootManifest = Path.Combine(sourceFolder, "manifest.json");
            string? direct = null;
            if (File.Exists(rootManifest))
            {
                var (parsed, error) = _packages.TryParseManifest(File.ReadAllText(rootManifest), sourceFolder);
                if (error is not null) { manifestError = error; return; }
                manifest = parsed;
            }
            else
            {
                direct = FindEntryPage(sourceFolder);
                if (direct is null) { manifestError = "That folder has no html entry page."; return; }
            }
            if (manifest is null)
                folder = _packages.CopyHtmlFolder(sourceFolder, out manifestError);
            else
                folder = sourceFolder;
        }).ConfigureAwait(false);
        if (manifestError is not null) return (null, manifestError);
        if (folder is null) return (null, "That folder could not be added.");
        try
        {
            string name = manifest?.Name ?? new DirectoryInfo(sourceFolder).Name;
            string? preview = manifest?.Preview;
            var item = new LiveWallpaperItem(Guid.NewGuid().ToString("N"), LiveKind.Html,
                name, folder, preview, DateTime.UtcNow);
            var items = _store.LoadLibrary();
            items.Add(item);
            _store.SaveLibrary(items);
            return (item, null);
        }
        catch (Exception ex)
        {
            return (null, "That folder could not be added: " + ex.Message);
        }
    }

    public async Task<(LiveWallpaperItem? Item, string? Error)> AddPackageAsync(string zipPath)
    {
        string? folder = null;
        LiveManifest? manifest = null;
        string? error = null;
        await Task.Run(() =>
        {
            var result = _packages.ImportZip(zipPath);
            folder = result.Folder;
            manifest = result.Manifest;
            error = result.Error;
        }).ConfigureAwait(false);
        if (error is not null || folder is null) return (null, error ?? "That package could not be imported.");
        try
        {
            string? entry = manifest?.Entry ?? FindEntryPage(folder);
            if (entry is null)
            {
                DeleteTree(folder);
                return (null, "That package has no html entry page.");
            }
            var item = new LiveWallpaperItem(Guid.NewGuid().ToString("N"), LiveKind.Html,
                manifest?.Name ?? Path.GetFileName(zipPath), folder, manifest?.Preview, DateTime.UtcNow);
            var items = _store.LoadLibrary();
            items.Add(item);
            _store.SaveLibrary(items);
            return (item, null);
        }
        catch (Exception ex)
        {
            return (null, "That package could not be added: " + ex.Message);
        }
    }

    public bool Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var items = _store.LoadLibrary();
        int index = items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        items[index] = items[index] with { Name = name.Trim() };
        _store.SaveLibrary(items);
        return true;
    }

    public bool Remove(string id)
    {
        var items = _store.LoadLibrary();
        var item = items.FirstOrDefault(i => i.Id == id);
        if (item is null) return false;
        items.Remove(item);
        _store.SaveLibrary(items);
        DeleteTree(item.Path);
        return true;
    }

    public async Task<StorageItemThumbnail?> ReadVideoThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            return await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView).AsTask().ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(LiveWallpaperStore.RootDir);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;
            Directory.Delete(full, recursive: true);
        }
        catch { }
    }

    private static string? FindEntryPage(string folder)
    {
        try
        {
            foreach (var candidate in new[] { "index.html", "index.htm", "wallpaper.html", "main.html" })
            {
                string full = Path.Combine(folder, candidate);
                if (File.Exists(full)) return candidate;
            }
            var first = Directory.EnumerateFiles(folder, "*.html", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (first is not null) return Path.GetFileName(first);
        }
        catch { }
        return null;
    }

    private static async Task<TimeSpan?> ReadVideoDurationAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            var source = MediaSource.CreateFromStorageFile(file);
            await source.OpenAsync().AsTask().ConfigureAwait(false);
            var duration = source.Duration;
            source.Dispose();
            return duration;
        }
        catch { return null; }
    }
}
