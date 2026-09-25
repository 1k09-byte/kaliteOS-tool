using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

public sealed class GameLibraryService
{
    private static readonly object LogGate = new();
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Games", "games-page.log");
    internal static readonly string _iconCacheDirStatic = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Games");

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}" +
                (exception is null ? string.Empty : $"\n{exception}") + Environment.NewLine;
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
            Debug.WriteLine("[Games] " + message + (exception is null ? string.Empty : $": {exception.Message}"));
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Games");
    private string CachePath => Path.Combine(_dataDirectory, "games-cache.json");

    public async Task<IReadOnlyList<GameModel>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Log("LoadAsync started");
        try
        {
            var cached = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
            Log($"Cache read completed: {cached.Count} games");
            if (cached.Count > 0)
                return cached;
        }
        catch (Exception ex) { Log("Cache read failed; falling back to scan", ex); }

        Log("No usable cache; starting launcher scan");
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GameModel>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Log("RefreshAsync scan started");
        var games = await Task.Run(() =>
        {
            var result = new List<GameModel>();
            try { result.AddRange(ScanSteam()); Log($"Steam scan complete: {result.Count} total candidates"); } catch (Exception ex) { Log("Steam scan failed", ex); }
            try { result.AddRange(ScanEpic()); Log($"Epic scan complete: {result.Count} total candidates"); } catch (Exception ex) { Log("Epic scan failed", ex); }
            try { result.AddRange(ScanRoblox()); Log($"Roblox scan complete: {result.Count} total candidates"); } catch (Exception ex) { Log("Roblox scan failed", ex); }
            try { result.AddRange(ScanGog()); Log($"GOG scan complete: {result.Count} total candidates"); } catch (Exception ex) { Log("GOG scan failed", ex); }
            try { result.AddRange(ScanUbisoft()); Log($"Ubisoft scan complete: {result.Count} total candidates"); } catch (Exception ex) { Log("Ubisoft scan failed", ex); }
            var merged = result
                .Where(g => !string.IsNullOrWhiteSpace(g.Title))
                .GroupBy(g => $"{g.Launcher}:{g.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Auto-art pass: EVERY game gets art without any per-game work.
            // Steam already has CDN URLs; everyone else gets their exe icon
            // extracted to the local cache automatically at scan time.
            foreach (var game in merged)
            {
                if (!string.IsNullOrEmpty(game.CoverImageUrl)) continue;
                var exe = FindLaunchExe(game);
                if (exe is null)
                {
                    Log($"[AutoArt] No exe found for '{game.Title}' ({game.Launcher}); wordmark fallback will render");
                    continue;
                }
                game.CoverImageUrl = ExtractIconCache(exe, $"{game.Launcher}-{game.Id}");
                Log($"[AutoArt] {(string.IsNullOrEmpty(game.CoverImageUrl) ? "FAILED" : "extracted")} icon for '{game.Title}' from {exe}");
            }
            return merged;
        }, cancellationToken).ConfigureAwait(false);

        await WriteCacheAsync(games, cancellationToken).ConfigureAwait(false);
        Log($"RefreshAsync completed: {games.Count} games");
        return games;
    }

    public async Task AddManualAsync(GameModel game, CancellationToken cancellationToken = default)
    {
        var games = (await ReadCacheAsync(cancellationToken).ConfigureAwait(false)).ToList();
        games.RemoveAll(g => g.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
        games.Add(game);
        await WriteCacheAsync(games.OrderBy(g => g.Title).ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveManualAsync(GameModel game, CancellationToken cancellationToken = default)
    {
        if (!game.IsManual) return;
        var games = (await ReadCacheAsync(cancellationToken).ConfigureAwait(false)).ToList();
        games.RemoveAll(g => g.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
        await WriteCacheAsync(games, cancellationToken).ConfigureAwait(false);
    }

    public static bool Launch(GameModel game)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(game.LaunchCommand)) return false;
            var isUri = game.LaunchCommand.Contains("://", StringComparison.Ordinal);
            var info = isUri
                ? new ProcessStartInfo(game.LaunchCommand) { UseShellExecute = true }
                : new ProcessStartInfo(game.LaunchCommand) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(game.LaunchCommand) ?? string.Empty };
            Process.Start(info);
            return true;
        }
        catch { return false; }
    }

    public static void OpenInstallFolder(GameModel game)
    {
        if (!Directory.Exists(game.InstallLocation)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.InstallLocation}\"") { UseShellExecute = true });
    }

    private async Task<List<GameModel>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath)) return new List<GameModel>();
        await using var stream = File.OpenRead(CachePath);
        return await JsonSerializer.DeserializeAsync<List<GameModel>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new List<GameModel>();
    }

    private async Task WriteCacheAsync(IReadOnlyList<GameModel> games, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        var temporary = CachePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, games, JsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, CachePath, true);
    }

    private static IEnumerable<GameModel> ScanSteam()
    {
        string? steamPath = null;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            steamPath = key?.GetValue("SteamPath") as string;
        Log($"Steam registry path: {steamPath ?? "<missing>"}");
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath)) yield break;

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        foreach (var library in libraries)
        {
            var manifests = Path.Combine(library, "steamapps");
            if (!Directory.Exists(manifests)) continue;
            foreach (var file in Directory.EnumerateFiles(manifests, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(file);
                var appId = MatchVdf(text, "appid");
                var name = MatchVdf(text, "name");
                var installDir = MatchVdf(text, "installdir");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) continue;
                var location = Path.Combine(manifests, "common", installDir ?? string.Empty);
                var steamCover = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";
                Log($"[Scan] Steam '{name}' (appid {appId}) → cover URL: {steamCover}");
                yield return new GameModel
                {
                    Id = appId,
                    Title = name,
                    Launcher = GameLauncher.Steam,
                    CoverImageUrl = steamCover,
                    HeroImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_hero.jpg",
                    InstallLocation = location,
                    LaunchCommand = $"steam://rungameid/{appId}",
                    InstalledUtc = File.GetLastWriteTimeUtc(file)
                };
            }
        }
    }

    private static IEnumerable<GameModel> ScanEpic()
    {
        var result = new List<GameModel>();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        Log($"Epic manifest directory: {directory}");
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var p) ? p.GetString() ?? string.Empty : string.Empty;
                var title = Get("DisplayName");
                var location = Get("InstallLocation");
                var ns = Get("CatalogNamespace");
                var item = Get("CatalogItemId");
                var app = Get("AppName");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(location)) continue;
                var command = string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(app)
                    ? location
                    : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
                result.Add(new GameModel
                {
                    Id = string.IsNullOrWhiteSpace(app) ? Path.GetFileNameWithoutExtension(file) : app,
                    Title = title,
                    Launcher = GameLauncher.Epic,
                    InstallLocation = location,
                    LaunchCommand = command,
                    InstalledUtc = File.GetLastWriteTimeUtc(file)
                });
            }
            catch { }
        }
        return result;
    }

    private static IEnumerable<GameModel> ScanRoblox()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions")
        };

        var candidates = new List<(string Exe, string Folder, DateTime InstalledUtc)>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var folder in Directory.EnumerateDirectories(root, "version-*"))
                {
                    var exe = Path.Combine(folder, "RobloxPlayerBeta.exe");
                    if (File.Exists(exe)) candidates.Add((exe, folder, File.GetLastWriteTimeUtc(exe)));
                }
            }
            catch { }
        }

        var latest = candidates.OrderByDescending(c => c.InstalledUtc).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latest.Exe)) yield break;
        yield return new GameModel
        {
            Id = "roblox-player",
            Title = "Roblox",
            Launcher = GameLauncher.Roblox,
            InstallLocation = latest.Folder,
            LaunchCommand = latest.Exe,
            InstalledUtc = latest.InstalledUtc,
            CoverImageUrl = ExtractIconCache(latest.Exe, "roblox-player")
        };
    }

    private static IEnumerable<GameModel> ScanGog()
    {
        foreach (var rootPath in new[] { @"SOFTWARE\\GOG.com\\Games", @"SOFTWARE\\WOW6432Node\\GOG.com\\Games" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id);
                var title = key?.GetValue("gameName") as string;
                var location = key?.GetValue("path") as string;
                var executable = key?.GetValue("exe") as string;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(location)) continue;
                var launch = !string.IsNullOrWhiteSpace(executable)
                    ? Path.Combine(location, executable)
                    : location;
                yield return new GameModel
                {
                    Id = id,
                    Title = title,
                    Launcher = GameLauncher.Gog,
                    InstallLocation = location,
                    LaunchCommand = launch,
                    InstalledUtc = Directory.Exists(location) ? Directory.GetLastWriteTimeUtc(location) : null
                };
            }
        }
    }

    private static IEnumerable<GameModel> ScanUbisoft()
    {
        foreach (var rootPath in new[] { @"SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher\\Installs", @"SOFTWARE\\Ubisoft\\Launcher\\Installs" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id);
                var location = key?.GetValue("InstallDir") as string;
                var title = key?.GetValue("DisplayName") as string ?? id;
                if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location)) continue;
                var executable = Directory.EnumerateFiles(location, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                yield return new GameModel
                {
                    Id = id,
                    Title = title,
                    Launcher = GameLauncher.Ubisoft,
                    InstallLocation = location,
                    LaunchCommand = executable ?? location,
                    InstalledUtc = Directory.GetLastWriteTimeUtc(location)
                };
            }
        }
    }

    /// <summary>
    /// Extracts the executable's icon at the highest available resolution and
    /// saves it as a PNG. ExtractAssociatedIcon only yields 32×32, which looks
    /// blurry on a 200×300 card - so we use SHDefExtractIcon to ask the shell
    /// for the 256×256 (jumbo) icon first, then fall back progressively.
    /// </summary>
    private static string ExtractIconCache(string exePath, string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return string.Empty;
            var cacheDir = Path.Combine(_iconCacheDirStatic, "Icons");
            Directory.CreateDirectory(cacheDir);
            var target = Path.Combine(cacheDir, id + ".png");
            if (File.Exists(target)) return new Uri(target).AbsoluteUri;

            foreach (var size in new[] { 256, 48, 32 })
            {
                var hIcon = SHDefExtractIcon(exePath, 0, 0, out var hLarge, IntPtr.Zero, (uint)size);
                if (hIcon != 0 || hLarge == IntPtr.Zero) continue;
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(hLarge);
                    using var bitmap = icon.ToBitmap();
                    if (bitmap.Width < 16) continue;
                    bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                    Log($"[Icon] Extracted {bitmap.Width}x{bitmap.Height} icon from {Path.GetFileName(exePath)}");
                    return new Uri(target).AbsoluteUri;
                }
                finally { DestroyIcon(hLarge); }
            }

            // Last resort: 32×32 ExtractAssociatedIcon (better than nothing).
            using var fallback = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (fallback is null) return string.Empty;
            using var fbBitmap = fallback.ToBitmap();
            fbBitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            Log($"[Icon] Fallback 32x32 icon extracted from {Path.GetFileName(exePath)}");
            return new Uri(target).AbsoluteUri;
        }
        catch (Exception ex)
        {
            Log($"Icon extraction failed for {exePath}", ex);
            return string.Empty;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint SHDefExtractIcon(string iconPath, int iconIndex, uint flags,
        out IntPtr hIconLarge, IntPtr hIconSmall, uint size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Brand accent color used for the last-resort wordmark fallback.</summary>
    public static Windows.UI.Color GetLauncherAccent(GameLauncher launcher) => launcher switch
    {
        GameLauncher.Steam => Windows.UI.Color.FromArgb(255, 26, 46, 68),      // steam navy
        GameLauncher.Epic => Windows.UI.Color.FromArgb(255, 43, 43, 43),        // epic charcoal
        GameLauncher.Roblox => Windows.UI.Color.FromArgb(255, 226, 35, 26),     // roblox red
        GameLauncher.Gog => Windows.UI.Color.FromArgb(255, 134, 30, 46),        // gog maroon
        GameLauncher.Riot => Windows.UI.Color.FromArgb(255, 17, 17, 17),        // riot black
        GameLauncher.Ubisoft => Windows.UI.Color.FromArgb(255, 0, 112, 222),    // ubisoft blue
        GameLauncher.BattleNet => Windows.UI.Color.FromArgb(255, 0, 126, 214),  // blizzard blue
        _ => Windows.UI.Color.FromArgb(255, 68, 68, 76)                          // manual neutral
    };

    /// <summary>
    /// Finds the most likely game executable for icon extraction: the launch
    /// command itself if it's a local exe, otherwise the largest exe in the
    /// install folder (biggest exe is almost always the game, not a launcher
    /// stub or crash handler).
    /// </summary>
    private static string? FindLaunchExe(GameModel game)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchCommand)
                && !game.LaunchCommand.Contains("://", StringComparison.Ordinal)
                && File.Exists(game.LaunchCommand))
                return game.LaunchCommand;

            if (string.IsNullOrWhiteSpace(game.InstallLocation) || !Directory.Exists(game.InstallLocation))
                return null;

            return Directory.EnumerateFiles(game.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static string? MatchVdf(string text, string key)
    {
        return Regex.Match(text, $@"""{Regex.Escape(key)}""\s+""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
    }
}
