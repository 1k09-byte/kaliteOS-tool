// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use, but the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of the source code, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace kaliteConfig.Services;

/// <summary>
/// One setting's driver-published metadata, as cached on disk: its value type, its
/// default, and the labels the driver gives its values.
/// </summary>
public sealed record NvidiaDriverMetadataEntry(
    string? Type, uint? Default, IReadOnlyList<(uint Value, string Label)> Options)
{
    public static readonly NvidiaDriverMetadataEntry Empty =
        new(null, null, Array.Empty<(uint, string)>());
}

/// <summary>
/// Keeps the driver's per-setting metadata on disk between runs.
///
/// The reason this exists is measured, not guessed. Reading the metadata for the 159
/// settings a profile offers costs about 24 seconds, because every property of the
/// SDK's SettingInfo is a separate native call of roughly 20 milliseconds and the read
/// path needs three of them plus every value name. None of it changes while a driver
/// is loaded - asking twice for one setting returned an identical list - and the page
/// re-reads on every profile switch and after every apply. Caching it in memory alone
/// only helps after the first load, so it is also written here, keyed on the driver
/// library's file version: a driver update changes that version and the cache is
/// rebuilt rather than showing values the new driver has altered.
///
/// A missing, stale, corrupt or unreadable cache is not an error. Every call here is
/// best-effort and returns empty, and the caller simply reads the driver instead -
/// slowly, but correctly.
/// </summary>
public static class NvidiaDriverMetadataCache
{
    private const int FormatVersion = 1;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "Nvidia", "driver-metadata.json");

    /// <summary>
    /// The driver library's file version, which changes with every driver update and
    /// so is what the cache is keyed on. Empty when it cannot be read, in which case
    /// the cache is simply never trusted across runs.
    /// </summary>
    public static string DriverVersion()
    {
        foreach (string path in DriverLibraryPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileVersion)) return info.FileVersion;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return string.Empty;
    }

    private static IEnumerable<string> DriverLibraryPaths()
    {
        // The loaded module first: the driver may come from the driver store rather
        // than System32, and that is the copy the settings actually came from.
        foreach (string loaded in LoadedDriverLibrary())
            yield return loaded;

        string system = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (File.Exists(system)) yield return system;
    }

    private static List<string> LoadedDriverLibrary()
    {
        var found = new List<string>();
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                string file = module.FileName;
                if (!string.IsNullOrEmpty(file)
                    && Path.GetFileName(file).Equals("nvapi64.dll", StringComparison.OrdinalIgnoreCase))
                    found.Add(file);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return found;
    }

    /// <summary>
    /// Loads the cache, but only if it was written for the driver that is installed
    /// now. Returns an empty map on any mismatch or failure, which is the same as a
    /// cold cache: correct, just slower.
    /// </summary>
    public static Dictionary<uint, NvidiaDriverMetadataEntry> Load(
        string? driverVersion = null, string? path = null)
    {
        var result = new Dictionary<uint, NvidiaDriverMetadataEntry>();
        try
        {
            string file = path ?? FilePath;
            if (!File.Exists(file)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            if (!root.TryGetProperty("formatVersion", out var fv)
                || fv.ValueKind != JsonValueKind.Number || fv.GetInt32() != FormatVersion)
                return result;

            string wanted = driverVersion ?? DriverVersion();
            if (wanted.Length == 0) return result;
            if (!root.TryGetProperty("driverVersion", out var dv)
                || dv.ValueKind != JsonValueKind.String
                || !string.Equals(dv.GetString(), wanted, StringComparison.Ordinal))
                return result;

            if (!root.TryGetProperty("settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in settings.EnumerateObject())
            {
                if (!TryParseId(property.Name, out uint id)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                result[id] = ReadEntry(property.Value);
            }
            return result;
        }
        catch (JsonException) { return new Dictionary<uint, NvidiaDriverMetadataEntry>(); }
        catch (IOException) { return new Dictionary<uint, NvidiaDriverMetadataEntry>(); }
        catch (UnauthorizedAccessException) { return new Dictionary<uint, NvidiaDriverMetadataEntry>(); }
    }

    private static NvidiaDriverMetadataEntry ReadEntry(JsonElement element)
    {
        string? type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        uint? def = element.TryGetProperty("default", out var d)
            && d.ValueKind == JsonValueKind.Number && d.TryGetUInt32(out uint dv) ? dv : null;

        var options = new List<(uint, string)>();
        if (element.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in opts.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Array) continue;
                var parts = option.EnumerateArray().ToList();
                if (parts.Count != 2) continue;
                if (parts[0].ValueKind != JsonValueKind.Number || !parts[0].TryGetUInt32(out uint value)) continue;
                if (parts[1].ValueKind != JsonValueKind.String) continue;
                options.Add((value, parts[1].GetString() ?? string.Empty));
            }
        }
        return new NvidiaDriverMetadataEntry(type, def, options);
    }

    public static string Serialize(IReadOnlyDictionary<uint, NvidiaDriverMetadataEntry> entries, string driverVersion)
    {
        var settings = new Dictionary<string, object>();
        foreach (var pair in entries.OrderBy(p => p.Key))
        {
            var entry = pair.Value ?? NvidiaDriverMetadataEntry.Empty;
            settings[$"0x{pair.Key:X8}"] = new
            {
                type = entry.Type,
                @default = entry.Default,
                options = entry.Options.Select(o => new object[] { o.Value, o.Label }).ToArray(),
            };
        }
        return JsonSerializer.Serialize(new
        {
            formatVersion = FormatVersion,
            driverVersion,
            settings,
        });
    }

    /// <summary>
    /// Writes the cache with the given entries merged over whatever is already on
    /// disk, so a run that only learned a few new settings keeps the ones it already
    /// knew instead of replacing them.
    ///
    /// Overwriting was the original bug and it was invisible: each save dropped every
    /// entry the previous run had paid for, so the cache never grew past one batch and
    /// a fresh process stayed cold forever. Merging is what makes it converge.
    ///
    /// Silently does nothing if the file cannot be read or written.
    /// </summary>
    public static void MergeAndSave(
        IReadOnlyDictionary<uint, NvidiaDriverMetadataEntry> additions, string? path = null)
    {
        try
        {
            string file = path ?? FilePath;
            var merged = Load(path: file);
            foreach (var pair in additions) merged[pair.Key] = pair.Value;
            Save(merged, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes the cache. Silently does nothing if the file cannot be written.</summary>
    public static void Save(IReadOnlyDictionary<uint, NvidiaDriverMetadataEntry> entries, string? path = null)
    {
        try
        {
            string file = path ?? FilePath;
            string? dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(file, Serialize(entries, DriverVersion()));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryParseId(string text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
