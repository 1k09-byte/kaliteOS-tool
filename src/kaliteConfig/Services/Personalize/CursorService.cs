using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

public sealed class CursorService : ICursorService
{
    public const string CursorsKey = @"Control Panel\Cursors";
    public const string MouseKey = @"Control Panel\Mouse";
    public const string DesktopKey = @"Control Panel\Desktop";

    private const uint SpiFlags = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;
    private const long MaxCursorBytes = 2 * 1024 * 1024;

    public const int MinPointerSize = 16;
    public const int MaxPointerSize = 128;
    public const int MinCaretWidth = 1;
    public const int MaxCaretWidth = 20;
    public const int MaxWheelLines = 100;
    public const int MaxTrails = 12;

    public static readonly string[] Roles =
    {
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No",
        "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand",
    };

    private static readonly (string Role, string File)[] DefaultFiles =
    {
        ("Arrow", "aero_arrow.cur"), ("Help", "aero_helpsel.cur"),
        ("AppStarting", "aero_working.ani"), ("Wait", "aero_busy.ani"),
        ("Crosshair", ""), ("IBeam", ""), ("NWPen", "aero_pen.cur"), ("No", "aero_unavail.cur"),
        ("SizeNS", "aero_ns.cur"), ("SizeWE", "aero_ew.cur"), ("SizeNWSE", "aero_nwse.cur"),
        ("SizeNESW", "aero_nesw.cur"), ("SizeAll", "aero_move.cur"), ("UpArrow", "aero_up.cur"),
        ("Hand", "aero_link.cur"),
    };

    private static readonly (string Role, string[] Aliases)[] RoleAliases =
    {
        ("Arrow", new[] { "arrow", "normal", "aero_arrow" }),
        ("Help", new[] { "help", "helpsel", "aero_helpsel" }),
        ("AppStarting", new[] { "appstarting", "working", "aero_working" }),
        ("Wait", new[] { "wait", "busy", "aero_busy" }),
        ("Crosshair", new[] { "crosshair", "cross", "precision" }),
        ("IBeam", new[] { "ibeam", "beam", "text" }),
        ("NWPen", new[] { "nwpen", "pen", "handwriting", "aero_pen" }),
        ("No", new[] { "no", "unavail", "unavailable" }),
        ("SizeNS", new[] { "sizens", "ns", "vert" }),
        ("SizeWE", new[] { "sizewe", "ew", "horz" }),
        ("SizeNWSE", new[] { "sizenwse", "nwse", "diag1" }),
        ("SizeNESW", new[] { "sizenesw", "nesw", "diag2" }),
        ("SizeAll", new[] { "sizeall", "move" }),
        ("UpArrow", new[] { "uparrow", "up" }),
        ("Hand", new[] { "hand", "link" }),
    };

    private readonly SettingApplier _applier;
    private readonly string _store;

    public CursorService(SettingApplier applier, string storeRoot)
    {
        _applier = applier;
        _store = Path.GetFullPath(storeRoot).TrimEnd('\\') + '\\';
    }

    private static string WindowsCursorDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "cursors") + '\\';

    public IReadOnlyList<string> RoleNames => Roles;

    public IReadOnlyDictionary<string, string> CurrentScheme()
    {
        var map = new Dictionary<string, string>();
        foreach (var role in Roles)
        {
            var e = HkcuRegistry.ReadEntry(CursorsKey, role);
            map[role] = e.Exists ? Convert.ToString(e.Data, CultureInfo.InvariantCulture) ?? "" : "";
        }
        return map;
    }

    public IReadOnlyDictionary<string, string> WindowsDefaultScheme()
    {
        var map = new Dictionary<string, string>();
        foreach (var (role, file) in DefaultFiles)
            map[role] = file.Length == 0 ? "" : Path.Combine(WindowsCursorDir, file);
        return map;
    }

    public int CurrentPointerSize() => ReadInt(CursorsKey, "CursorBaseSize", 32);

    public MouseOptions CurrentMouse() => new(
        ReadInt(MouseKey, "MouseSensitivity", 10),
        ReadInt(MouseKey, "MouseSpeed", 0) == 1,
        ReadInt(MouseKey, "MouseTrails", 0),
        ReadInt(DesktopKey, "WheelScrollLines", 3));

    public int CurrentCaretWidth() => ReadInt(DesktopKey, "CaretWidth", 1);

    public (string? Path, string? Error) ImportCursorFile(string sourcePath)
    {
        var error = ValidateCursorFile(sourcePath, out var full);
        if (error is not null) return (null, error);

        Directory.CreateDirectory(_store);
        var dest = Path.Combine(_store, Guid.NewGuid().ToString("N") + Path.GetExtension(full).ToLowerInvariant());
        File.Copy(full, dest, overwrite: false);

        var copyError = ValidateCursorFile(dest, out var destFull);
        if (copyError is not null)
        {
            TryDelete(dest);
            return (null, copyError);
        }
        return (destFull, null);
    }

    public IReadOnlyList<CursorPack> Packs()
    {
        var packs = new List<CursorPack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, source) in PackRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                if (!seen.Add(name)) continue;

                int count = 0;
                try
                {
                    count = Directory.EnumerateFiles(dir)
                        .Count(f => Path.GetExtension(f).ToLowerInvariant() is ".cur" or ".ani");
                }
                catch { continue; }

                if (count == 0) continue;
                packs.Add(new CursorPack(name, dir, source, count));
            }
        }

        return packs;
    }

    private IEnumerable<(string Root, string Source)> PackRoots()
    {
        yield return (Path.Combine(_store.TrimEnd('\\'), "cursor-packs"), "My packs");

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 4 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "Assets", "cursors");
            if (Directory.Exists(candidate)) { yield return (candidate, "Bundled"); break; }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
    }

    public SchemeScan ScanSchemeFolder(string folder)
    {
        var mapping = new Dictionary<string, string>();
        var skipped = new List<string>();
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) { return new SchemeScan(mapping, new[] { ex.Message }); }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return new SchemeScan(mapping, new[] { "Network folders are not supported." });
        if (!Directory.Exists(full))
            return new SchemeScan(mapping, new[] { "The folder does not exist." });

        foreach (var file in Directory.EnumerateFiles(full))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".cur" or ".ani")) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(name + ": links are not followed.");
                continue;
            }

            var role = MatchRole(Path.GetFileNameWithoutExtension(file));
            if (role is null || mapping.ContainsKey(role))
            {
                skipped.Add(name + ": no matching or unique pointer role.");
                continue;
            }

            var (path, error) = ImportCursorFile(file);
            if (path is null) skipped.Add(name + ": " + error);
            else mapping[role] = path;
        }
        return new SchemeScan(mapping, skipped);
    }

    public IReadOnlyList<SettingResult> ApplyScheme(IReadOnlyDictionary<string, string> roleToPath)
    {
        var normalized = new Dictionary<string, string>();
        foreach (var (role, path) in roleToPath)
        {
            if (!Roles.Contains(role))
                return Fail("cursor.role." + role, "Unknown pointer role.");
            if (string.IsNullOrEmpty(path))
            {
                normalized[role] = "";
                continue;
            }
            var error = ValidateCursorFile(path, out var full);
            if (error is null && !IsAllowedCursorPath(full))
                error = "Cursor files must come from the app's cursor library or the Windows cursors folder.";
            if (error is not null)
                return Fail("cursor.role." + role, error);
            normalized[role] = full;
        }

        var defs = normalized.Select(kv => new SettingDef(
            "cursor.role." + kv.Key, CursorsKey, kv.Key, Microsoft.Win32.RegistryValueKind.String, kv.Value, ReloadCursors))
            .ToList();
        return _applier.Apply(defs);
    }

    public IReadOnlyList<SettingResult> ApplyPointerSize(int size)
    {
        if (size < MinPointerSize || size > MaxPointerSize)
            return Fail("cursor.size", $"Pointer size must be between {MinPointerSize} and {MaxPointerSize}.");
        return _applier.Apply(new[]
        {
            new SettingDef("cursor.size", CursorsKey, "CursorBaseSize", Microsoft.Win32.RegistryValueKind.DWord, size, SyncPointerSize),
        });
    }

    public IReadOnlyList<SettingResult> ApplyMouse(MouseOptions o)
    {
        if (o.PointerSpeed < 1 || o.PointerSpeed > 20)
            return Fail("cursor.mouse.speed", "Pointer speed must be between 1 and 20.");
        if (o.Trails < 0 || o.Trails > MaxTrails)
            return Fail("cursor.mouse.trails", $"Pointer trails must be between 0 and {MaxTrails}.");
        if (o.WheelLines < 1 || o.WheelLines > MaxWheelLines)
            return Fail("cursor.wheel", $"Scroll lines must be between 1 and {MaxWheelLines}.");

        string t1 = o.EnhancedPrecision ? "6" : "0";
        string t2 = o.EnhancedPrecision ? "10" : "0";
        string accel = o.EnhancedPrecision ? "1" : "0";

        return _applier.Apply(new[]
        {
            new SettingDef("cursor.mouse.speed", MouseKey, "MouseSensitivity", Microsoft.Win32.RegistryValueKind.String, o.PointerSpeed.ToString(CultureInfo.InvariantCulture), SyncSpeed),
            new SettingDef("cursor.mouse.t1", MouseKey, "MouseThreshold1", Microsoft.Win32.RegistryValueKind.String, t1, SyncMouseCurve),
            new SettingDef("cursor.mouse.t2", MouseKey, "MouseThreshold2", Microsoft.Win32.RegistryValueKind.String, t2, SyncMouseCurve),
            new SettingDef("cursor.mouse.accel", MouseKey, "MouseSpeed", Microsoft.Win32.RegistryValueKind.String, accel, SyncMouseCurve),
            new SettingDef("cursor.mouse.trails", MouseKey, "MouseTrails", Microsoft.Win32.RegistryValueKind.String, o.Trails.ToString(CultureInfo.InvariantCulture), SyncTrails),
            new SettingDef("cursor.wheel", DesktopKey, "WheelScrollLines", Microsoft.Win32.RegistryValueKind.String, o.WheelLines.ToString(CultureInfo.InvariantCulture), SyncWheel),
        });
    }

    public IReadOnlyList<SettingResult> ApplyCaretWidth(int width)
    {
        if (width < MinCaretWidth || width > MaxCaretWidth)
            return Fail("cursor.caret", $"Text cursor width must be between {MinCaretWidth} and {MaxCaretWidth}.");
        return _applier.Apply(new[]
        {
            new SettingDef("cursor.caret", DesktopKey, "CaretWidth", Microsoft.Win32.RegistryValueKind.DWord, width, SyncCaret),
        });
    }

    public IReadOnlyList<SettingResult> RevertSection() =>
        _applier.RevertWhere(
            e => e.Id.StartsWith("cursor.", StringComparison.Ordinal),
            new (string, Func<bool>)[]
            {
                ("cursors", ReloadCursors),
                ("mouse-speed", SyncSpeed),
                ("mouse-curve", SyncMouseCurve),
                ("trails", SyncTrails),
                ("wheel", SyncWheel),
                ("caret", SyncCaret),
            });

    public IReadOnlyList<SettingResult> ResetToWindowsDefaults()
    {
        var results = new List<SettingResult>();
        results.AddRange(ApplyScheme(WindowsDefaultScheme()));
        results.AddRange(ApplyPointerSize(32));
        results.AddRange(ApplyMouse(new MouseOptions(10, true, 0, 3)));
        results.AddRange(ApplyCaretWidth(1));
        return results;
    }

    private static bool ReloadCursors() =>
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);

    // SPI_SETCURSORS alone does not resize the pointer; Windows only picks up CursorBaseSize
    // when the undocumented 0x2029 action is called with the pixel size. Reload cursors after it.
    private static bool SyncPointerSize()
    {
        int size = ReadInt(CursorsKey, "CursorBaseSize", 32);
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETCURSORSIZE, 0, new IntPtr(size), 0);
        return ReloadCursors();
    }

    private static bool SyncSpeed() =>
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETMOUSESPEED, 0,
            new IntPtr(ReadInt(MouseKey, "MouseSensitivity", 10)), SpiFlags);

    private static bool SyncMouseCurve()
    {
        var curve = new[]
        {
            ReadInt(MouseKey, "MouseThreshold1", 0),
            ReadInt(MouseKey, "MouseThreshold2", 0),
            ReadInt(MouseKey, "MouseSpeed", 0),
        };
        return NativeMethods.SystemParametersInfoIntArray(NativeMethods.SPI_SETMOUSE, 0, curve, SpiFlags);
    }

    private static bool SyncTrails() =>
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETMOUSETRAILS,
            (uint)ReadInt(MouseKey, "MouseTrails", 0), IntPtr.Zero, SpiFlags);

    private static bool SyncWheel() =>
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETWHEELSCROLLLINES,
            (uint)ReadInt(DesktopKey, "WheelScrollLines", 3), IntPtr.Zero, SpiFlags);

    private static bool SyncCaret() =>
        NativeMethods.SystemParametersInfoPtr(NativeMethods.SPI_SETCARETWIDTH,
            (uint)ReadInt(DesktopKey, "CaretWidth", 1), IntPtr.Zero, SpiFlags);

    private static IReadOnlyList<SettingResult> Fail(string id, string message) =>
        new[] { new SettingResult(id, false, message) };

    private bool IsAllowedCursorPath(string fullPath) =>
        fullPath.StartsWith(_store, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(WindowsCursorDir, StringComparison.OrdinalIgnoreCase);

    private static string? MatchRole(string nameWithoutExt)
    {
        var key = nameWithoutExt.Trim().ToLowerInvariant();
        foreach (var (role, aliases) in RoleAliases)
            if (aliases.Contains(key)) return role;
        return null;
    }

    public static string? ValidateCursorFile(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return "No file was given.";

        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) { return ex.Message; }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return "Network paths are not supported.";

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is not (".cur" or ".ani")) return "Only .cur and .ani cursor files are allowed.";
        if (!File.Exists(fullPath)) return "The cursor file does not exist.";

        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > MaxCursorBytes)
            return "The cursor file is empty or larger than 2 MB.";

        var header = new byte[12];
        using (var fs = File.OpenRead(fullPath))
        {
            if (fs.Read(header, 0, header.Length) < 4) return "The cursor file is too small.";
        }
        bool headerOk = ext == ".cur"
            ? header[0] == 0 && header[1] == 0 && header[2] == 2 && header[3] == 0
            : header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
              header[8] == 'A' && header[9] == 'C' && header[10] == 'O' && header[11] == 'N';
        if (!headerOk) return "The file header does not match a cursor file.";

        var handle = NativeMethods.LoadImage(IntPtr.Zero, fullPath, NativeMethods.IMAGE_CURSOR, 0, 0, NativeMethods.LR_LOADFROMFILE);
        if (handle == IntPtr.Zero) return "Windows could not load this cursor file.";
        NativeMethods.DestroyCursor(handle);
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static int ReadInt(string subKey, string name, int fallback)
    {
        var e = HkcuRegistry.ReadEntry(subKey, name);
        if (!e.Exists) return fallback;
        var text = Convert.ToString(e.Data, CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
