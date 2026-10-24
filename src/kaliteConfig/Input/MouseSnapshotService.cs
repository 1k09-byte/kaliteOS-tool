using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace kaliteConfig.Input;

public sealed class MouseSnapshotValue
{
    public string DeviceId { get; set; } = string.Empty;
    public string Setting { get; set; } = string.Empty;
    public string Original { get; set; } = string.Empty;
}

public interface IMouseSnapshotService
{
    void CaptureIfNeeded(string deviceId, string setting, string original);
    bool TryGetOriginal(string deviceId, string setting, out string original);
    IReadOnlyList<MouseSnapshotValue> ForDevice(string deviceId);
    void ClearDevice(string deviceId);
    void ClearAll();
}

public sealed class MouseSnapshotService : IMouseSnapshotService
{
    private readonly object _gate = new();
    private readonly string _path;
    private List<MouseSnapshotValue> _entries;

    public MouseSnapshotService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "input", "snapshots.json");
        _entries = Load();
    }

    public void CaptureIfNeeded(string deviceId, string setting, string original)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(setting)) return;
        lock (_gate)
        {
            bool exists = _entries.Any(e =>
                string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Setting, setting, StringComparison.OrdinalIgnoreCase));
            if (exists) return;
            _entries.Add(new MouseSnapshotValue { DeviceId = deviceId, Setting = setting, Original = original ?? string.Empty });
            SaveLocked();
        }
    }

    public bool TryGetOriginal(string deviceId, string setting, out string original)
    {
        lock (_gate)
        {
            var found = _entries.FirstOrDefault(e =>
                string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Setting, setting, StringComparison.OrdinalIgnoreCase));
            original = found?.Original ?? string.Empty;
            return found != null;
        }
    }

    public IReadOnlyList<MouseSnapshotValue> ForDevice(string deviceId)
    {
        lock (_gate)
            return _entries.Where(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void ClearDevice(string deviceId)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            SaveLocked();
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    private List<MouseSnapshotValue> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<MouseSnapshotValue>();
            string text = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<MouseSnapshotValue>>(text) ?? new List<MouseSnapshotValue>();
        }
        catch
        {
            return new List<MouseSnapshotValue>();
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        catch
        {
        }
    }
}
