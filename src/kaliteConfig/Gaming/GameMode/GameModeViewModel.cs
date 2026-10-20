using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace kaliteConfig.Gaming.GameMode;

public sealed partial class GameModeViewModel : ObservableObject
{
    private readonly GameModeEngine _engine;
    private readonly DispatcherQueue _dispatcher;
    private bool _updating;

    public GameModeViewModel(GameModeEngine engine)
    {
        _engine = engine;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _engine.Changed += OnEngineChanged;
        StatusText = string.Empty;
        StatusSeverity = InfoBarSeverity.Informational;
        GamePathInput = string.Empty;
        NeverTouchInput = string.Empty;
        JournalText = string.Empty;
        PreviewSummary = string.Empty;
        PresetIndex = 1;
        DebounceMs = 750;
        Refresh();
    }

    public ObservableCollection<GameModeAffectedRow> Affected { get; } = new();
    public ObservableCollection<GameModeLogRow> LogRows { get; } = new();
    public ObservableCollection<GameModeGameEntry> Games { get; } = new();
    public ObservableCollection<string> NeverTouch { get; } = new();
    public IReadOnlyList<string> LogFilters { get; } = new[] { "All results", "Applied", "Skipped or refused", "Downgraded", "Could not restore" };

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial double DebounceMs { get; set; }

    [ObservableProperty]
    public partial int PresetIndex { get; set; }

    [ObservableProperty]
    public partial int LogFilterIndex { get; set; }

    [ObservableProperty]
    public partial string GamePathInput { get; set; }

    [ObservableProperty]
    public partial string NeverTouchInput { get; set; }

    [ObservableProperty]
    public partial string JournalText { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string PreviewSummary { get; set; }

    public string EngineState => _engine.State.ToString();

    partial void OnEnabledChanged(bool value)
    {
        if (_updating) return;
        _engine.SetEnabled(value);
    }

    partial void OnDebounceMsChanged(double value)
    {
        if (_updating) return;
        _engine.SetDebounce((int)value);
    }

    partial void OnPresetIndexChanged(int value)
    {
        if (_updating) return;
        _engine.SetPreset((GameModePreset)Math.Clamp(value, 0, 3));
    }

    partial void OnLogFilterIndexChanged(int value) => ApplyLogFilter();

    private void OnEngineChanged()
    {
        if (_dispatcher.HasThreadAccess) Refresh();
        else _dispatcher.TryEnqueue(Refresh);
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            StatusText = _engine.StatusText;
            IsActive = _engine.State == GameModeState.Active;
            StatusSeverity = _engine.State switch
            {
                GameModeState.Active => InfoBarSeverity.Success,
                GameModeState.Pending => InfoBarSeverity.Warning,
                GameModeState.Releasing => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };
            Enabled = _engine.Settings.Enabled;
            DebounceMs = _engine.Settings.ClampedDebounce;
            PresetIndex = (int)_engine.Settings.Preset;
            JournalText = _engine.PendingJournalCount == 0
                ? "Nothing is held by Game Mode."
                : $"{_engine.PendingJournalCount} journal entr(ies) held; restore happens on release.";

            Replace(Affected, _engine.Affected);
            Replace(Games, _engine.Games);
            Replace(NeverTouch, _engine.NeverTouch);
            ApplyLogFilter();
            OnPropertyChanged(nameof(EngineState));
        }
        finally
        {
            _updating = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private void ApplyLogFilter()
    {
        var rows = _engine.LogRows.AsEnumerable();
        rows = LogFilterIndex switch
        {
            1 => rows.Where(r => r.Result.Contains("Applied", StringComparison.OrdinalIgnoreCase)),
            2 => rows.Where(r => r.Result.Contains("Refused", StringComparison.OrdinalIgnoreCase) || r.Result.Contains("Skipped", StringComparison.OrdinalIgnoreCase)),
            3 => rows.Where(r => r.Result.Contains("Downgraded", StringComparison.OrdinalIgnoreCase)),
            4 => rows.Where(r => r.Result.Contains("Could not restore", StringComparison.OrdinalIgnoreCase)),
            _ => rows,
        };
        Replace(LogRows, rows);
    }

    public void ResumeAll()
    {
        _engine.ResumeEverything();
        Refresh();
    }

    public void Preview()
    {
        var rows = _engine.Preview();
        Replace(Affected, rows);
        PreviewSummary = rows.Count == 0
            ? "Preview found nothing to plan. Add a game to the list first."
            : $"Preview only. {rows.Count} background process(es) would be planned; nothing was changed.";
        Refresh();
    }

    public void AddGamePath()
    {
        _engine.AddGamePath(GamePathInput.Trim());
        GamePathInput = string.Empty;
        Refresh();
    }

    public void AddFocusedApp()
    {
        var hwnd = kaliteConfig.Native.NativeMethods.GameMode.GetForegroundWindow();
        if (ForegroundWatcher.TryResolveForeground(hwnd, out int pid, out _, out _))
            _engine.AddGame(pid);
        Refresh();
    }

    public void AddNeverTouch()
    {
        _engine.AddNeverTouch(NeverTouchInput);
        NeverTouchInput = string.Empty;
        Refresh();
    }

    public void RemoveNeverTouch(string pattern)
    {
        _engine.RemoveNeverTouch(pattern);
        Refresh();
    }

    public void RemoveGame(string name)
    {
        _engine.RemoveGame(name);
        Refresh();
    }

    public void ClearLog()
    {
        _engine.ClearLog();
        Refresh();
    }

    public void CopyLog()
    {
        var builder = new StringBuilder();
        foreach (var row in LogRows)
            builder.AppendLine($"{row.Time}\t{row.Process}\t{row.Action}\t{row.Result}\t{row.Message}");
        try
        {
            var package = new DataPackage();
            package.SetText(builder.ToString());
            Clipboard.SetContent(package);
        }
        catch
        {
        }
    }

    public void ExportLog()
    {
        try
        {
            var path = System.IO.Path.Combine(GameModeSettingsStore.RootDirectory, $"game-mode-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            System.IO.Directory.CreateDirectory(GameModeSettingsStore.RootDirectory);
            var builder = new StringBuilder();
            foreach (var row in _engine.LogRows)
                builder.AppendLine($"{row.Time}\t{row.Process}\t{row.Action}\t{row.Result}\t{row.Message}");
            System.IO.File.WriteAllText(path, builder.ToString());
        }
        catch
        {
        }
    }
}
