using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace kaliteConfig.Gaming.GameMode;

public sealed class GameModeLog
{
    private readonly object _gate = new();
    private readonly List<GameModeLogRow> _rows = new();
    private readonly string _path;
    private const int MaxRows = 500;
    private const long MaxFileBytes = 262144;

    public GameModeLog(string path)
    {
        _path = path;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch
        {
        }
    }

    public event Action<GameModeLogRow>? Added;

    public IReadOnlyList<GameModeLogRow> Snapshot()
    {
        lock (_gate) return _rows.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _rows.Clear();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    public void Add(string process, string action, string result, string message)
    {
        AddCore(process, action, result, message);
    }

    public void Add(string first, string second, string third, string fourth, string fifth)
    {
        AddCore(first == "-" ? second : first, third, fourth, fifth);
    }

    private void AddCore(string process, string action, string result, string message)
    {
        var row = new GameModeLogRow(DateTime.Now.ToString("HH:mm:ss"), process, action, result, message);
        lock (_gate)
        {
            _rows.Insert(0, row);
            if (_rows.Count > MaxRows) _rows.RemoveRange(MaxRows, _rows.Count - MaxRows);
        }
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{process}\t{action}\t{result}\t{message}{Environment.NewLine}";
            File.AppendAllText(_path, line);
            var info = new FileInfo(_path);
            if (info.Length > MaxFileBytes)
            {
                var keep = File.ReadLines(_path).TakeLast(200);
                File.WriteAllLines(_path, keep);
            }
        }
        catch
        {
        }
        try { Added?.Invoke(row); } catch { }
    }
}
