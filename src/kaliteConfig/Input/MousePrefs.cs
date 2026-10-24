using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace kaliteConfig.Input;

public sealed class MousePrefsData
{
    public Dictionary<string, string> Nicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ForceShow { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ReapplyOnConnect { get; set; }
    public Dictionary<string, Dictionary<string, string>> Applied { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IMousePrefStore
{
    MousePrefsData Data { get; }
    void Save();
}

public sealed class MousePrefStore : IMousePrefStore
{
    private readonly string _path;

    public MousePrefStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "input", "prefs.json");
        Data = Load();
    }

    public MousePrefsData Data { get; }

    public void Save()
    {
        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        catch { }
    }

    private MousePrefsData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new MousePrefsData();
            string text = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<MousePrefsData>(text) ?? new MousePrefsData();
        }
        catch
        {
            return new MousePrefsData();
        }
    }
}
