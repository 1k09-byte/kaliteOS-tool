using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace kaliteConfig.Services.LiveWallpaper;

public sealed class WallpaperPackageService
{
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv", ".webm", ".mkv" };
    public static readonly string[] HtmlExtensions = { ".html", ".htm", ".js", ".css", ".json", ".png", ".jpg", ".jpeg", ".svg", ".webp", ".gif", ".mp4", ".webm", ".woff", ".woff2", ".ttf", ".otf" };

    private const int MaxFiles = 1000;
    private const long MaxTotalBytes = 1L << 30;
    private const long MaxVideoBytes = 4L << 30;
    private const double MaxCompressionRatio = 100.0;

    public static bool IsLocalPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            return Path.IsPathFullyQualified(path);
        }
        catch { return false; }
    }

    public static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var allowed in VideoExtensions)
            if (ext == allowed) return true;
        return false;
    }

    public (LiveManifest? Manifest, string? Error) TryParseManifest(string json, string baseDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                return (null, "Unsupported manifest schema version.");
            if (!root.TryGetProperty("name", out var nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
                return (null, "The manifest has no name.");
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "html")
                return (null, "The manifest type must be html.");
            if (!root.TryGetProperty("entry", out var entryEl) || string.IsNullOrWhiteSpace(entryEl.GetString()))
                return (null, "The manifest has no entry page.");
            string entry = entryEl.GetString()!;
            if (!entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                return (null, "The manifest entry must be an html page.");
            if (!File.Exists(Path.Combine(baseDir, entry)))
                return (null, "The manifest entry page was not found in the package.");
            string? preview = null;
            if (root.TryGetProperty("preview", out var previewEl) && !string.IsNullOrWhiteSpace(previewEl.GetString()))
            {
                string candidate = Path.Combine(baseDir, previewEl.GetString()!);
                if (File.Exists(candidate)) preview = candidate;
            }
            Dictionary<string, LivePropertyDef>? props = null;
            if (root.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                props = new Dictionary<string, LivePropertyDef>();
                foreach (var prop in propsEl.EnumerateObject())
                {
                    var def = ParseProperty(prop.Name, prop.Value);
                    if (def is null) return (null, $"Property '{prop.Name}' is not valid.");
                    props[prop.Name] = def;
                }
            }
            return (new LiveManifest(1, nameEl.GetString()!, "html", entry, preview, props), null);
        }
        catch (Exception ex)
        {
            return (null, "The manifest could not be read: " + ex.Message);
        }
    }

    private static LivePropertyDef? ParseProperty(string key, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("type", out var typeEl)) return null;
        string type = typeEl.GetString() ?? string.Empty;
        if (type is not ("boolean" or "number" or "choice" or "color")) return null;
        string label = el.TryGetProperty("label", out var labelEl) ? (labelEl.GetString() ?? key) : key;
        double min = el.TryGetProperty("min", out var minEl) && minEl.TryGetDouble(out var minV) ? minV : 0;
        double max = el.TryGetProperty("max", out var maxEl) && maxEl.TryGetDouble(out var maxV) ? maxV : 100;
        double step = el.TryGetProperty("step", out var stepEl) && stepEl.TryGetDouble(out var stepV) && stepV > 0 ? stepV : 1;
        string def = el.TryGetProperty("default", out var defEl) ? defEl.ToString() : string.Empty;
        string[] options = Array.Empty<string>();
        if (el.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var o in optEl.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is string s) list.Add(s);
            options = list.ToArray();
        }
        if (type == "choice" && options.Length == 0) return null;
        if (max < min) return null;
        return new LivePropertyDef(key, type, label, min, max, step, def, options);
    }

    public (string? Folder, LiveManifest? Manifest, string? Error) ImportZip(string zipPath)
    {
        try
        {
            if (!IsLocalPath(zipPath) || !File.Exists(zipPath))
                return (null, null, "That file could not be found.");
            if (!string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
                return (null, null, "Only .zip packages can be imported.");
            string dest = Path.Combine(LiveWallpaperStore.PackagesDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            long total = 0;
            int count = 0;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative) || relative.Contains("..")) continue;
                    string ext = Path.GetExtension(relative).ToLowerInvariant();
                    bool allowed = false;
                    foreach (var a in HtmlExtensions)
                        if (ext == a) { allowed = true; break; }
                    if (!allowed && ext == ".json" && string.Equals(Path.GetFileName(relative), "manifest.json", StringComparison.OrdinalIgnoreCase))
                        allowed = true;
                    if (!allowed) continue;
                    if (++count > MaxFiles)
                    {
                        Directory.Delete(dest, true);
                        return (null, null, "That package has too many files.");
                    }
                    total += entry.Length;
                    if (total > MaxTotalBytes || (entry.CompressedLength > 1024 && entry.Length > entry.CompressedLength * MaxCompressionRatio))
                    {
                        Directory.Delete(dest, true);
                        return (null, null, "That package is too large.");
                    }
                    string target = Path.GetFullPath(Path.Combine(dest, relative));
                    if (!target.StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            LiveManifest? manifest = null;
            string manifestPath = Path.Combine(dest, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var (parsed, error) = TryParseManifest(File.ReadAllText(manifestPath), dest);
                if (error is not null)
                {
                    Directory.Delete(dest, true);
                    return (null, null, error);
                }
                manifest = parsed;
            }
            return (dest, manifest, null);
        }
        catch (Exception ex)
        {
            return (null, null, "That package could not be imported: " + ex.Message);
        }
    }

    public string? CopyHtmlFolder(string source, out string? error)
    {
        error = null;
        try
        {
            if (!IsLocalPath(source) || !Directory.Exists(source))
            {
                error = "That folder could not be found.";
                return null;
            }
            string dest = Path.Combine(LiveWallpaperStore.PackagesDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            long total = 0;
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                bool allowed = false;
                foreach (var a in HtmlExtensions)
                    if (ext == a) { allowed = true; break; }
                if (!allowed) continue;
                var info = new FileInfo(file);
                if (info.Length > MaxVideoBytes)
                {
                    error = "That folder has a file that is too large.";
                    Directory.Delete(dest, true);
                    return null;
                }
                if (++count > MaxFiles || (total += info.Length) > MaxTotalBytes)
                {
                    error = "That folder is too large to import.";
                    Directory.Delete(dest, true);
                    return null;
                }
                string relative = Path.GetRelativePath(source, file);
                string target = Path.GetFullPath(Path.Combine(dest, relative));
                if (!target.StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            return dest;
        }
        catch (Exception ex)
        {
            error = "That folder could not be imported: " + ex.Message;
            return null;
        }
    }
}
