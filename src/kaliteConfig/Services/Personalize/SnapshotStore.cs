using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

public sealed class SnapshotEntry
{
    public string Id { get; set; } = "";
    public string SubKey { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "String";
    public string? Value { get; set; }
}

public sealed class SnapshotStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SnapshotEntry> _entries;

    public SnapshotStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        _entries = Load();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "personalize", "snapshot.json");

    public string StoragePath => _path;

    public bool Has(string id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    public IReadOnlyList<SnapshotEntry> All()
    {
        lock (_gate) return _entries.Values.ToList();
    }

    public void CaptureIfNeeded(string id, string subKey, string name)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(id)) return;
            _entries[id] = ReadCurrent(id, subKey, name);
            Save();
        }
    }

    public static SnapshotEntry ReadCurrent(string id, string subKey, string name)
    {
        var entry = HkcuRegistry.ReadEntry(subKey, name);
        if (entry.Exists && entry.Kind is not (RegistryValueKind.DWord or RegistryValueKind.String))
            throw new NotSupportedException($"{subKey}\\{name} has type {entry.Kind}; only DWORD and string values are supported.");
        return new SnapshotEntry
        {
            Id = id,
            SubKey = subKey,
            Name = name,
            Existed = entry.Exists,
            Kind = entry.Kind == RegistryValueKind.DWord ? "DWord" : "String",
            Value = entry.Exists ? Convert.ToString(entry.Data, CultureInfo.InvariantCulture) : null,
        };
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id)) Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private Dictionary<string, SnapshotEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, SnapshotEntry>();
            var list = JsonSerializer.Deserialize<List<SnapshotEntry>>(File.ReadAllText(_path)) ?? new();
            return list.Where(e => !string.IsNullOrEmpty(e.Id)).ToDictionary(e => e.Id, e => e);
        }
        catch (JsonException)
        {
            try { File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); } catch { }
            return new Dictionary<string, SnapshotEntry>();
        }
    }
}
