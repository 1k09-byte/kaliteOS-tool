using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.ProcessOptimizer.Services;
using kaliteConfig.Services;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

/// <summary>Min/max-bucket decimation so spikes survive (never every-Nth-point). Pure.</summary>
public static class BenchmarkDecimate
{
    public static List<(double Min, double Max)> MinMaxBuckets(IReadOnlyList<float> frames, int maxBuckets)
    {
        var out_ = new List<(double Min, double Max)>();
        if (frames == null || frames.Count == 0 || maxBuckets <= 0) return out_;
        if (frames.Count <= maxBuckets)
        {
            foreach (float f in frames) out_.Add((f, f));
            return out_;
        }
        double per = (double)frames.Count / maxBuckets;
        for (int b = 0; b < maxBuckets; b++)
        {
            int s = (int)(b * per), e = Math.Min(frames.Count, (int)((b + 1) * per));
            if (e <= s) e = Math.Min(frames.Count, s + 1);
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = s; i < e; i++)
            {
                if (frames[i] < mn) mn = frames[i];
                if (frames[i] > mx) mx = frames[i];
            }
            out_.Add((mn, mx));
        }
        return out_;
    }
}

public sealed class CompareRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Short label for bars/legend - OFF/ON + pair for A/B runs.</summary>
    public string ShortLabel { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#4CC2FF";
    public double Avg { get; set; }
    public double P1 { get; set; }
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double Low1Count { get; set; }
    public double Low01Count { get; set; }
    public double AvgArith { get; set; }
    public double AvgHarm { get; set; }
    public double Median { get; set; }
    public double DeltaAvgPct { get; set; }
    public double DeltaP1Pct { get; set; }
    public double DeltaLow1Pct { get; set; }
    public double DeltaLow01Pct { get; set; }
    public bool IsBaseline { get; set; }

    public string AvgText => $"{Avg:F1}";
    public string MedianText => $"{Median:F1}";
    public string Low1Text => $"{Low1Count:F1}";
    public string Low01Text => $"{Low01Count:F1}";
    public string DeltaAvgText => FmtDelta(DeltaAvgPct);
    public string DeltaLow1Text => FmtDelta(DeltaLow1Pct);
    public string DeltaLow01Text => FmtDelta(DeltaLow01Pct);
    private static string FmtDelta(double d) => $"{d:+0.0;-0.0;0}%";
}

public sealed partial class BenchmarkViewModel : ObservableObject
{
    private readonly BenchmarkCaptureService _capture = new();
    private readonly BenchmarkStoreService _store = new();
    private readonly BenchmarkHotkeyService _hotkey = new();
    private readonly BenchmarkSensorSampler _sensors = new();
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _captureCts;
    private DispatcherQueueTimer? _tickTimer;
    private DispatcherQueueTimer? _tenLeftTimer;
    private int _ticksLeft;
    private int _capturePid;
    private List<SensorSample> _sensorSamples = new();

    public ObservableCollection<TunerProcessRow> Targets { get; } = new();
    public ObservableCollection<BenchmarkRun> Runs { get; } = new();
    public ObservableCollection<BenchmarkRun> CompareRuns { get; } = new();
    public ObservableCollection<CompareRow> CompareRows { get; } = new();

    [ObservableProperty]
    public partial TunerProcessRow? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial BenchmarkRun? SelectedRun { get; set; }

    /// <summary>Most recent saved run, shown as a summary card on the Runs tab.</summary>
    [ObservableProperty]
    public partial BenchmarkRun? LastRun { get; set; }

    [ObservableProperty]
    public partial string LastRunSummary { get; set; } = "No runs yet.";

    [ObservableProperty]
    public partial BenchmarkStats? SelectedStats { get; set; }

    [ObservableProperty]
    public partial BenchmarkRun? BaselineRun { get; set; }

    [ObservableProperty]
    public partial BenchmarkDuration Duration { get; set; } = BenchmarkDuration.Seconds60;

    [ObservableProperty]
    public partial int CountdownSec { get; set; } = 3;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Pick a process, then Start capture.";

    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    /// <summary>
    /// Global flag other components (ThreadTuner polling, sensor timers) check
    /// so the tool itself stays out of the measurement during a capture.
    /// </summary>
    public static bool CaptureInProgress { get; private set; }

    partial void OnIsCapturingChanged(bool value) => CaptureInProgress = value;

    [ObservableProperty]
    public partial string CaptureStateText { get; set; } = "Idle";

    [ObservableProperty]
    public partial string CommentDraft { get; set; } = string.Empty;

    // ─── Cues + hotkey ──────────────────────────────────────────
    public List<CueMode> CueModes { get; } = new() { CueMode.Beeps, CueMode.Voice, CueMode.Off };

    [ObservableProperty]
    public partial CueMode CueModeSelected { get; set; } = CueService.Instance.Mode;

    [ObservableProperty]
    public partial double CueVolume { get; set; } = CueService.Instance.Volume;

    [ObservableProperty]
    public partial string VoiceNote { get; set; } = CueService.VoiceAvailable
        ? "Voice files found - Voice mode will speak."
        : "No voice files - add voice_started.wav + voice_stopped.wav to Assets/Sounds (Voice falls back to beeps).";

    [ObservableProperty]
    public partial bool ShowSensorOverlay { get; set; } = false;

    // ─── Auto A/B benchmark ─────────────────────────────────────

    /// <summary>Auto A/B armed: hotkey press runs OFF and ON captures back-to-back.</summary>
    [ObservableProperty]
    public partial bool AutoAbEnabled { get; set; }

    /// <summary>How many capture pairs (off+on) to run per hotkey press.</summary>
    [ObservableProperty]
    public partial int AutoAbRuns { get; set; } = 3;

    [ObservableProperty]
    public partial string AutoAbStatus { get; set; } = string.Empty;

    private bool _autoAbBusy;

    [ObservableProperty]
    public partial string SensorSummaryText { get; set; } = "No sensor data.";

    [ObservableProperty]
    public partial string ReportHeaderText { get; set; } = "Open a run in Runs to analyze it.";

    [ObservableProperty]
    public partial string? LiveCsvPath { get; set; }

    public int LivePid { get; private set; }

    [ObservableProperty]
    public partial string ConflictWarningText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HotkeyEnabled { get; set; } = true;

    public List<BenchmarkHotkeyService.HotkeyPreset> HotkeyPresets { get; } =
        new(BenchmarkHotkeyService.Presets);

    [ObservableProperty]
    public partial BenchmarkHotkeyService.HotkeyPreset SelectedHotkeyPreset { get; set; } =
        BenchmarkHotkeyService.Presets[0];

    [ObservableProperty]
    public partial string HotkeyStatus { get; set; } = string.Empty;

    partial void OnCueModeSelectedChanged(CueMode value) => CueService.Instance.Mode = value;
    partial void OnCueVolumeChanged(double value) => CueService.Instance.Volume = value;

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (value)
        {
            var (ok, msg) = _hotkey.Register(SelectedHotkeyPreset);
            HotkeyStatus = ok ? $"Hotkey {SelectedHotkeyPreset.Label} active." : msg;
            if (!ok) HotkeyEnabled = false;
        }
        else
        {
            _hotkey.Unregister();
            HotkeyStatus = "Hotkey off.";
        }
        SaveHotkeySettings();
    }

    partial void OnSelectedHotkeyPresetChanged(BenchmarkHotkeyService.HotkeyPreset value)
    {
        if (value == null) return;
        if (HotkeyEnabled)
        {
            var (ok, msg) = _hotkey.Register(value);
            HotkeyStatus = ok ? $"Hotkey {value.Label} active." : msg;
        }
        SaveHotkeySettings();
    }

    private static string HotkeySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "benchmark-hotkey.json");

    private sealed class HotkeySettings
    {
        public bool Enabled { get; set; } = true;
        public string Preset { get; set; } = "Ctrl+F10";
    }

    private void LoadHotkeySettings()
    {
        try
        {
            if (File.Exists(HotkeySettingsPath))
            {
                var s = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(HotkeySettingsPath));
                if (s != null)
                {
                    HotkeyEnabled = s.Enabled;
                    var match = HotkeyPresets.FirstOrDefault(p => p.Label == s.Preset);
                    if (match != null) SelectedHotkeyPreset = match;
                }
            }
        }
        catch { }
    }

    private void SaveHotkeySettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HotkeySettingsPath)!);
            File.WriteAllText(HotkeySettingsPath, JsonSerializer.Serialize(new HotkeySettings
            {
                Enabled = HotkeyEnabled,
                Preset = SelectedHotkeyPreset?.Label ?? "Ctrl+F10",
            }));
        }
        catch { }
    }

    public List<BenchmarkDuration> DurationOptions { get; } = new()
    {
        BenchmarkDuration.Manual, BenchmarkDuration.Seconds30,
        BenchmarkDuration.Seconds60, BenchmarkDuration.Minutes2,
    };

    public BenchmarkViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _capture.StateChanged += s => _dispatcher.TryEnqueue(() =>
        {
            CaptureStateText = s.ToString();
            OnCaptureState(s);
        });
        LoadHotkeySettings();
        _hotkey.Pressed += () => _dispatcher.TryEnqueue(ToggleCaptureFromHotkey);
        if (HotkeyEnabled)
        {
            var (ok, msg) = _hotkey.Register(SelectedHotkeyPreset);
            HotkeyStatus = ok ? $"Hotkey {SelectedHotkeyPreset.Label} active." : msg;
            if (!ok) HotkeyEnabled = false;
        }
        else HotkeyStatus = "Hotkey off.";
        _ = RefreshRunsAsync();
        _ = LoadTargetsAsync();
    }

    private void ToggleCaptureFromHotkey()
    {
        if (_autoAbBusy) { StatusText = "Auto A/B running - hotkey ignored."; return; }
        if (AutoAbEnabled) { _ = RunAutoAbAsync(); return; }
        if (IsCapturing) StopCapture();
        else _ = StartCaptureAsync();
    }

    /// <summary>
    /// Auto A/B: for N runs - capture with Gaming mode OFF (baseline), then
    /// toggle Gaming mode ON, capture again, toggle back OFF. Labels runs
    /// "A/B r1 off" / "A/B r1 on" so the compare card can baseline them.
    /// Requires a selected target (the game). Runs sequentially; safe to
    /// trigger from the in-game hotkey.
    /// </summary>
    private async Task RunAutoAbAsync()
    {
        if (_autoAbBusy || IsCapturing) return;
        if (SelectedTarget == null)
        {
            StatusText = "Auto A/B: pick the game in the target list first.";
            return;
        }
        if (Duration == BenchmarkDuration.Manual)
        {
            StatusText = "Auto A/B needs a fixed duration (not Manual).";
            return;
        }

        _autoAbBusy = true;
        int runs = Math.Clamp(AutoAbRuns, 1, 10);
        try
        {
            var gaming = App.Current.GamingMode;

            // A/B takes its own hold rather than exclusive control - it must not
            // tear down a session the user or a rule is holding. But that means
            // the "OFF" half of every pair is only genuinely off when nothing
            // else holds the session, and a GamingModeAuto rule for this very
            // game is the common case. Say so in the stored run comments instead
            // of silently producing a comparison that measures nothing.
            bool contaminated = gaming.IsHeldByOther(GameModeOwner.Benchmark);
            string caveat = contaminated
                ? " [WARNING: another holder is active - OFF runs are not truly off]"
                : "";

            string game = SelectedTarget.Name.Replace(".exe", "");
            var savedRunIds = new List<Guid>();
            await Task.Delay(1500); // let priorities settle

            if (contaminated)
            {
                AutoAbStatus = "Auto A/B: another holder is active";
                StatusText = "Auto A/B - Gaming mode is already held (a rule or the Threads page). " +
                             "Release it first, or the OFF runs include it and the comparison is meaningless.";
            }

            for (int r = 1; r <= runs; r++)
            {
                AutoAbStatus = $"Run {r}/{runs}: OFF";
                StatusText = $"Auto A/B {r}/{runs} - capturing baseline (gaming mode off)…";
                Guid? offId = await StartCaptureAsync($"{game} gaming mode off (A/B r{r}){caveat}");
                if (offId.HasValue) savedRunIds.Add(offId.Value);
                while (IsCapturing) await Task.Delay(500);
                await Task.Delay(2000); // settle between runs

                AutoAbStatus = $"Run {r}/{runs}: ON";
                var res = await gaming.AcquireAsync(
                    GameModeOwner.Benchmark, SelectedTarget.Pid, reason: "benchmark A/B harness");
                StatusText = $"Auto A/B {r}/{runs} - gaming mode ON: {res.Summary}";
                await Task.Delay(1500);
                Guid? onId = await StartCaptureAsync($"{game} gaming mode on (A/B r{r}){caveat}");
                if (onId.HasValue) savedRunIds.Add(onId.Value);
                while (IsCapturing) await Task.Delay(500);
                gaming.Release(GameModeOwner.Benchmark);
                StatusText = $"Auto A/B {r}/{runs} - gaming mode off again.";
                await Task.Delay(2000);
            }

            AutoAbStatus = $"Done: {runs} pair(s).";
            StatusText = $"Auto A/B complete - {runs * 2} runs saved. Opening Compare…";

            // Jump straight to Compare: baseline = first 'off' run, others added.
            {
                // RefreshRunsAsync repopulates via TryEnqueue, so it won't have
                // landed yet - pull the fresh runs from the store directly.
                var fresh = await _store.ListAsync();
                var firstOff = fresh.FirstOrDefault(r => r.Comment?.Contains("gaming mode off", StringComparison.OrdinalIgnoreCase) == true
                                                      && savedRunIds.Contains(r.Id));
                if (firstOff != null)
                {
                    CompareRuns.Clear();
                    CompareRows.Clear();
                    BaselineRun = firstOff;
                    SelectedRun = firstOff;
                    await AddToCompareAsync(firstOff);
                    foreach (var id in savedRunIds.Skip(1))
                    {
                        var run = fresh.FirstOrDefault(r => r.Id == id);
                        if (run != null) await AddToCompareAsync(run);
                    }
                    await RefreshRunsAsync();
                }
            }
            AutoAbNavigateRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AutoAbStatus = $"Failed: {ex.Message}";
            StatusText = $"Auto A/B failed: {ex.Message}";
            try { App.Current.GamingMode.Release(GameModeOwner.Benchmark); } catch { }
        }
        finally
        {
            _autoAbBusy = false;
        }
    }

    [RelayCommand]
    private void RunAutoAb()
    {
        if (_autoAbBusy) return;
        _ = RunAutoAbAsync();
    }

    private void StopCueTimers()
    {
        try { _tickTimer?.Stop(); } catch { }
        try { _tenLeftTimer?.Stop(); } catch { }
        _tickTimer = null;
        _tenLeftTimer = null;
    }

    /// <summary>Every sound hangs off a state-machine transition.</summary>
    private void OnCaptureState(CaptureState s)
    {
        var cues = CueService.Instance;
        switch (s)
        {
            case CaptureState.Armed:
                StopCueTimers();
                _ticksLeft = CountdownSec;
                if (_ticksLeft > 0)
                {
                    _tickTimer = _dispatcher.CreateTimer();
                    _tickTimer.Interval = TimeSpan.FromSeconds(1);
                    _tickTimer.Tick += (_, _) =>
                    {
                        if (_ticksLeft-- > 0) cues.Play(CueKind.Tick);
                        else try { _tickTimer?.Stop(); } catch { }
                    };
                    _tickTimer.Start();
                }
                break;
            case CaptureState.Recording:
                try { _tickTimer?.Stop(); } catch { }
                cues.Play(CueKind.Started);
                try { _sensors.Start(_capturePid); } catch { }
                if (Duration != BenchmarkDuration.Manual && (int)Duration >= 20)
                {
                    _tenLeftTimer = _dispatcher.CreateTimer();
                    _tenLeftTimer.Interval = TimeSpan.FromSeconds((int)Duration - 10);
                    _tenLeftTimer.IsRepeating = false;
                    _tenLeftTimer.Tick += (_, _) => cues.Play(CueKind.TenLeft);
                    _tenLeftTimer.Start();
                }
                break;
            case CaptureState.Finalizing:
                try { _sensorSamples = _sensors.Stop(); } catch { }
                break;
            case CaptureState.Done:
                StopCueTimers();
                cues.Play(CueKind.Stopped);
                break;
            case CaptureState.Failed:
                StopCueTimers();
                cues.Play(CueKind.Failed);
                break;
        }
    }

    partial void OnSelectedRunChanged(BenchmarkRun? value)
    {
        if (value == null) { SelectedStats = null; return; }
        _ = LoadRunDetailAsync(value);
    }

    [RelayCommand]
    private async Task LoadTargetsAsync()
    {
        try
        {
            var rows = await App.Current.ProcessTuning.ListProcessesAsync();
            _dispatcher.TryEnqueue(() =>
            {
                Targets.Clear();
                foreach (var r in rows.OrderByDescending(r => r.CpuPercent).Take(200)) Targets.Add(r);
                StatusText = $"Found {rows.Count} processes.";
                ConflictWarningText = FindConflicts(rows.Select(r => r.Name));
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Process list failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UseForeground()
    {
        if (BenchmarkCaptureService.TryGetForegroundProcess(out int pid, out string exe, out string err))
        {
            var match = Targets.FirstOrDefault(t => t.Pid == pid);
            if (match != null) SelectedTarget = match;
            StatusText = $"Foreground: {exe} (PID {pid}).";
        }
        else StatusText = err;
    }

    /// <summary>Raised when Auto A/B finishes so the page can switch to the Compare tab.</summary>
    public event Action? AutoAbNavigateRequested;

    public async Task<Guid?> AutoStartCaptureAsync(int pid, string exeName, string? commentOverride = null)
    {
        if (IsCapturing) return null;
        return await StartCaptureInternalAsync(pid, exeName, commentOverride);
    }

    [RelayCommand]
    private async Task<Guid?> StartCaptureAsync(string? commentOverride = null)
    {
        if (IsCapturing) return null;
        int pid;
        string exe;
        if (SelectedTarget != null) { pid = SelectedTarget.Pid; exe = SelectedTarget.Name; }
        else if (!BenchmarkCaptureService.TryGetForegroundProcess(out pid, out exe, out string err))
        {
            StatusText = "Select a process row first, or focus the game and press Use foreground.";
            return null;
        }
        return await StartCaptureInternalAsync(pid, exe, commentOverride);
    }

    private async Task<Guid?> StartCaptureInternalAsync(int pid, string exe, string? commentOverride = null)
    {
        string? savedComment = CommentDraft;
        if (commentOverride != null) CommentDraft = commentOverride;
        IsCapturing = true;
        _captureCts = new CancellationTokenSource();
        _capturePid = pid;
        LivePid = pid;
        _sensorSamples = new List<SensorSample>();
        LiveCsvPath = null;
        StatusText = $"Capturing {exe} (PID {pid})…";
        try
        {
            string csvPath = Path.Combine(Path.GetTempPath(), $"kalite-bench-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            LiveCsvPath = csvPath;
            var progress = new Progress<string>(s => StatusText = s);
            BenchmarkCaptureResult res = await _capture.CaptureAsync(
                pid, exe, Duration, CountdownSec, csvPath, _captureCts.Token, progress);
            var run = new BenchmarkRun
            {
                Game = exe.Replace(".exe", ""),
                Exe = exe,
                Date = DateTime.Now,
                DurationSec = res.ElapsedSec,
                Comment = CommentDraft?.Trim() ?? string.Empty,
                ColorHex = PickRunColor(Runs.Count),
                SystemInfo = SnapshotSystemInfo(),
                FrametimesMs = res.FrametimesMs,
                TimestampsMs = res.TimestampsMs,
                Sensors = new List<SensorSample>(_sensorSamples),
            };
            await _store.SaveAsync(run);
            try { File.Delete(csvPath); } catch { }
            await RefreshRunsAsync();
            CueService.Instance.Play(CueKind.Saved);
            _dispatcher.TryEnqueue(() =>
            {
                SelectedRun = Runs.FirstOrDefault(r => r.Id == run.Id);
                StatusText = $"Saved: {run.FrametimesMs.Length} frames in {run.DurationSec:F1}s.";
                if (commentOverride == null) CommentDraft = string.Empty;
                else CommentDraft = savedComment; // auto-A/B: keep the draft
            });
            return run.Id;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Capture stopped.";
            return null;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsCapturing = false;
            LiveCsvPath = null;
            LivePid = 0;
            _captureCts?.Dispose();
            _captureCts = null;
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        try { _captureCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private async Task RefreshRunsAsync()
    {
        try
        {
            var runs = await _store.ListAsync();
            BenchmarkRun? lastMeta = runs.Count > 0 ? runs[0] : null;
            BenchmarkStats? lastStats = null;
            if (lastMeta != null)
            {
                BenchmarkRun? full = await _store.LoadAsync(lastMeta.Id);
                if (full != null)
                {
                    lastStats = await Task.Run(() => ComputeRunStats(full));
                    if (lastMeta.FrametimesMs.Length == 0)
                    {
                        lastMeta.FrametimesMs = full.FrametimesMs;
                        lastMeta.TimestampsMs = full.TimestampsMs;
                    }
                }
            }
            _dispatcher.TryEnqueue(() =>
            {
                Runs.Clear();
                foreach (var r in runs) Runs.Add(r);
                PruneCompare();
                LastRun = lastMeta;
                LastRunSummary = lastStats == null || lastMeta == null
                    ? "No runs yet."
                    : $"{lastStats.AverageFps:F0} avg FPS · {lastStats.Low1TimeFps:F0} 1% low · {lastStats.MedianFps:F0} median · {lastStats.StutterCount} stutters · {lastMeta.DurationSec:F0}s";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Run list failed: {ex.Message}";
        }
    }

    private async Task LoadRunDetailAsync(BenchmarkRun meta)
    {
        try
        {
            BenchmarkRun? full = await _store.LoadAsync(meta.Id);
            if (full == null) return;
            BenchmarkStats stats = await Task.Run(() => ComputeRunStats(full));
            SensorSummary sens = await Task.Run(() => BenchmarkSensorStats.Summarize(full.Sensors));
            _dispatcher.TryEnqueue(() =>
            {
                int idx = Runs.IndexOf(meta);
                if (idx >= 0)
                {
                    Runs[idx].FrametimesMs = full.FrametimesMs;
                    Runs[idx].TimestampsMs = full.TimestampsMs;
                    Runs[idx].Sensors = full.Sensors;
                }
                SelectedStats = stats;
                SensorSummaryText = FormatSensorSummary(sens);
                ReportHeaderText = $"{full.Game} · {full.Date:yyyy-MM-dd HH:mm} · {full.DurationSec:F0}s" +
                    (string.IsNullOrWhiteSpace(full.Comment) ? "" : $" · {full.Comment}") +
                    $" · {full.SystemInfo.CpuName}" +
                    (string.IsNullOrWhiteSpace(full.SystemInfo.PowerPlan) ? "" : " · plan " + full.SystemInfo.PowerPlan) +
                    (full.SystemInfo.GamingMode ? " · GamingMode" : "") +
                    (string.IsNullOrWhiteSpace(full.SystemInfo.CpuSetsPartition) ? "" : " · " + full.SystemInfo.CpuSetsPartition);
                StatusText = $"{full.Game}: {stats.AverageFps:F0} avg · {stats.Low1TimeFps:F0} 1% low · {stats.StutterCount} stutters.";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteRunAsync(BenchmarkRun? run)
    {
        run ??= SelectedRun;
        if (run == null) return;
        await _store.DeleteAsync(run.Id);
        CompareRuns.Remove(run);
        if (BaselineRun == run) BaselineRun = null;
        await RefreshRunsAsync();
        if (SelectedRun == run) SelectedRun = null;
        RefreshCompare();
    }

    [RelayCommand]
    private async Task SaveCommentAsync()
    {
        if (SelectedRun == null) return;
        await _store.RenameAsync(SelectedRun.Id, CommentDraft ?? string.Empty);
        SelectedRun.Comment = CommentDraft ?? string.Empty;
        StatusText = "Comment saved.";
    }

    // ─── Compare / A-B ──────────────────────────────────────────────
    internal static readonly string[] ComparePalette = new[]
    {
        "#4CC2FF", "#FF9F43", "#3DDC84", "#B985FF", "#FF6B6B", "#FFD60A",
    };

    private string PickRunColor(int index) =>
        ComparePalette[Math.Abs(index) % ComparePalette.Length];

    [RelayCommand]
    private async Task AddToCompareAsync(BenchmarkRun? run)
    {
        run ??= SelectedRun;
        if (run == null || CompareRuns.Contains(run) || CompareRuns.Count >= 6) return;
        // The run list stores metadata only - frametimes load on demand.
        // Without them StatsFor() returns null and the row silently drops.
        await EnsureRunLoadedAsync(run);
        // Same default blue on everything is unreadable: guarantee a distinct color.
        if (CompareRuns.Any(r => string.Equals(r.ColorHex, run.ColorHex, StringComparison.OrdinalIgnoreCase)))
        {
            var used = new HashSet<string>(CompareRuns.Select(r => r.ColorHex), StringComparer.OrdinalIgnoreCase);
            string pick = ComparePalette.FirstOrDefault(c => !used.Contains(c))
                ?? ComparePalette[CompareRuns.Count % ComparePalette.Length];
            run.ColorHex = pick;
            _ = _store.UpdateColorAsync(run.Id, pick);
        }
        CompareRuns.Add(run);
        if (BaselineRun == null) BaselineRun = run;
        RefreshCompare();
    }

    /// <summary>Populate frametimes for a run that only has metadata.</summary>
    private async Task EnsureRunLoadedAsync(BenchmarkRun run)
    {
        if (run.FrametimesMs is { Length: > 0 }) return;
        try
        {
            var full = await _store.LoadAsync(run.Id);
            if (full == null) return;
            run.FrametimesMs = full.FrametimesMs;
            run.TimestampsMs = full.TimestampsMs;
            run.Sensors = full.Sensors;
            var listed = Runs.FirstOrDefault(r => r.Id == run.Id);
            if (listed != null && !ReferenceEquals(listed, run))
            {
                listed.FrametimesMs = full.FrametimesMs;
                listed.TimestampsMs = full.TimestampsMs;
                listed.Sensors = full.Sensors;
            }
        }
        catch { /* compare degrades to no row for this run */ }
    }

    [RelayCommand]
    private void RemoveFromCompare(BenchmarkRun? run)
    {
        if (run == null) return;
        CompareRuns.Remove(run);
        if (BaselineRun == run) BaselineRun = CompareRuns.FirstOrDefault();
        RefreshCompare();
    }

    [RelayCommand]
    private void ClearCompare()
    {
        CompareRuns.Clear();
        CompareRows.Clear();
        BaselineRun = null;
    }

    [RelayCommand]
    private void SetBaseline(BenchmarkRun? run)
    {
        if (run == null || !CompareRuns.Contains(run)) return;
        BaselineRun = run;
        RefreshCompare();
    }

    private void PruneCompare()
    {
        var ids = new HashSet<Guid>(Runs.Select(r => r.Id));
        for (int i = CompareRuns.Count - 1; i >= 0; i--)
            if (!ids.Contains(CompareRuns[i].Id)) CompareRuns.RemoveAt(i);
        if (BaselineRun != null && !CompareRuns.Contains(BaselineRun))
            BaselineRun = CompareRuns.FirstOrDefault();
        if (CompareRuns.Count > 0) RefreshCompare();
    }

    private void RefreshCompare()
    {
        // Auto-heal: runs stored before palette colors existed share one
        // color - spread them so bars are distinguishable.
        var distinct = new HashSet<string>(CompareRuns.Select(r => r.ColorHex), StringComparer.OrdinalIgnoreCase);
        if (CompareRuns.Count > 1 && distinct.Count == 1)
        {
            for (int ci = 0; ci < CompareRuns.Count; ci++)
            {
                string pick = ComparePalette[ci % ComparePalette.Length];
                CompareRuns[ci].ColorHex = pick;
                _ = _store.UpdateColorAsync(CompareRuns[ci].Id, pick);
            }
        }
        CompareRows.Clear();
        if (CompareRuns.Count == 0 || BaselineRun == null) return;
        BenchmarkStats? baseStats = StatsFor(BaselineRun);
        foreach (var r in CompareRuns)
        {
            BenchmarkStats? s = StatsFor(r);
            if (s == null || baseStats == null) continue;
            CompareRows.Add(new CompareRow
            {
                Id = r.Id,
                Name = string.IsNullOrWhiteSpace(r.Comment) ? $"{r.Game} {r.Date:HH:mm}" : r.Comment,
                ShortLabel = ShortLabelFor(r),
                ColorHex = r.ColorHex,
                Avg = s.AverageFps,
                P1 = s.P1Fps,
                Low1 = s.Low1TimeFps,
                Low01 = s.Low01TimeFps,
                Low1Count = s.Low1CountFps,
                Low01Count = s.Low01CountFps,
                AvgArith = s.ArithmeticAvgFps,
                AvgHarm = s.HarmonicAvgFps,
                Median = s.MedianFps,
                DeltaAvgPct = Pct(s.AverageFps, baseStats.AverageFps),
                DeltaP1Pct = Pct(s.P1Fps, baseStats.P1Fps),
                DeltaLow1Pct = Pct(s.Low1CountFps, baseStats.Low1CountFps),
                DeltaLow01Pct = Pct(s.Low01CountFps, baseStats.Low01CountFps),
                IsBaseline = r.Id == BaselineRun.Id,
            });
        }
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(ConclusionText));
    }

    /// <summary>
    /// Plain-language conclusion for the whole compare set: averages the
    /// ON-vs-OFF deltas across pairs when A/B runs are present, otherwise
    /// compares every run against the baseline.
    /// </summary>
    public string ConclusionText
    {
        get
        {
            if (CompareRuns.Count == 0 || BaselineRun == null) return string.Empty;
            var loaded = CompareRuns.Where(r => StatsFor(r) != null).ToList();
            if (loaded.Count < 2) return string.Empty;

            var baseStats = StatsFor(BaselineRun);
            if (baseStats == null) return string.Empty;

            // A/B pairs: group ON runs with the OFF baseline of the same pair.
            var pairs = new List<(string On, string Off, double AvgD, double Low1D)>();
            foreach (var onRun in loaded.Where(r => IsAbRun(r, on: true)))
            {
                var offRun = loaded.FirstOrDefault(r => IsAbRun(r, on: false)
                    && SamePair(r, onRun));
                var onStats = StatsFor(onRun);
                var offStats = offRun != null ? StatsFor(offRun) : null;
                if (onStats == null || offStats == null) continue;
                pairs.Add((ShortLabelFor(onRun), ShortLabelFor(offRun),
                    Pct(onStats.AverageFps, offStats.AverageFps),
                    Pct(onStats.Low1TimeFps, offStats.Low1TimeFps)));
            }
            if (pairs.Count > 0)
            {
                double avgD = pairs.Average(p => p.AvgD);
                double lowD = pairs.Average(p => p.Low1D);
                string verdict = Math.Abs(avgD) < 3.0 && Math.Abs(lowD) < 3.0
                    ? "within run-to-run noise"
                    : avgD > 0 ? "a measurable win" : "a measurable loss";
                string perPair = string.Join(", ",
                    pairs.Select(p => $"{p.On} vs {p.Off}: avg {p.AvgD:+0.0;-0.0}%, 1% low {p.Low1D:+0.0;-0.0}%"));
                return $"Conclusion: Gaming mode is {verdict}. {perPair}.";
            }

            // Non-A/B: best/worst against baseline.
            var others = loaded.Where(r => r.Id != BaselineRun.Id).ToList();
            if (others.Count == 0) return string.Empty;
            var ranked = others
                .Select(r => (Run: r, D: Pct(StatsFor(r)!.AverageFps, baseStats.AverageFps)))
                .OrderByDescending(x => x.D)
                .ToList();
            var best = ranked[0];
            var worst = ranked[^1];
            string nm(BenchmarkRun r) => string.IsNullOrWhiteSpace(r.Comment) ? ShortLabelFor(r) : r.Comment;
            return $"Conclusion: {nm(best.Run)} is fastest (avg {best.D:+0.0;-0.0}% vs baseline); " +
                   $"{nm(worst.Run)} is slowest (avg {worst.D:+0.0;-0.0}%).";
        }
    }

    private static bool IsAbRun(BenchmarkRun r, bool on) =>
        (r.Comment ?? string.Empty).Contains(on ? "gaming mode on" : "gaming mode off", StringComparison.OrdinalIgnoreCase);

    private static bool SamePair(BenchmarkRun a, BenchmarkRun b)
    {
        var ma = Regex.Match(a.Comment ?? "", @"A/B r(\d+)", RegexOptions.IgnoreCase);
        var mb = Regex.Match(b.Comment ?? "", @"A/B r(\d+)", RegexOptions.IgnoreCase);
        return ma.Success && mb.Success && ma.Groups[1].Value == mb.Groups[1].Value;
    }

    private static double Pct(double v, double b) => b > 0 ? 100.0 * (v - b) / b : 0;

    /// <summary>
    /// Compact bar/legend label: A/B runs become "OFF r1" / "ON r1", other
    /// runs fall back to comment tail or time so names stay readable.
    /// </summary>
    internal static string ShortLabelFor(BenchmarkRun r)
    {
        string c = r.Comment ?? string.Empty;
        bool on = c.Contains("gaming mode on", StringComparison.OrdinalIgnoreCase);
        bool off = c.Contains("gaming mode off", StringComparison.OrdinalIgnoreCase);
        if (on || off)
        {
            var m = Regex.Match(c, @"A/B r(\d+)", RegexOptions.IgnoreCase);
            string pair = m.Success ? $" r{m.Groups[1].Value}" : "";
            return (on ? "ON" : "OFF") + pair;
        }
        if (!string.IsNullOrWhiteSpace(c))
            return c.Length > 18 ? c[..18] + "…" : c;
        return $"{r.Date:HH:mm}";
    }

    private static BenchmarkStats? StatsFor(BenchmarkRun r)
    {
        if (r.FrametimesMs == null || r.FrametimesMs.Length == 0) return null;
        try { return ComputeRunStats(r); }
        catch { return null; }
    }

    /// <summary>
    /// Stats with warmup/cooldown edges trimmed (first 2 s + last 1 s):
    /// attach/tab artifacts must not define the lows. Real mid-run stalls
    /// stay in the data and surface as stutter events.
    /// </summary>
    internal static BenchmarkStats ComputeRunStats(BenchmarkRun r) =>
        BenchmarkStatsService.Compute(
            BenchmarkStatsService.WindowByTime(r.FrametimesMs, r.TimestampsMs));

    /// <summary>A/B verdict: only claims a win past run-to-run noise (~3%).</summary>
    public string VerdictText
    {
        get
        {
            if (CompareRuns.Count != 2 || BaselineRun == null) return "Add 2 runs to Compare for an A/B verdict.";
            var other = CompareRuns.FirstOrDefault(r => r.Id != BaselineRun.Id);
            if (other == null) return string.Empty;
            var b = StatsFor(BaselineRun);
            var o = StatsFor(other);
            if (b == null || o == null) return "Load both runs (open them in Runs) before comparing.";
            double d = Pct(o.Low1CountFps, b.Low1CountFps);
            string otherName = string.IsNullOrWhiteSpace(other.Comment) ? other.Game : other.Comment;
            string baseName = string.IsNullOrWhiteSpace(BaselineRun.Comment) ? BaselineRun.Game : BaselineRun.Comment;
            if (Math.Abs(d) < 3.0)
                return $"Within noise ({d:+0.0;-0.0}% on 1% lows) - {otherName} vs {baseName}.";
            return $"{otherName} vs {baseName}: {d:+0.0;-0.0}% on 1% lows " +
                   $"({(d > 0 ? "faster" : "slower")}, avg {Pct(o.AverageFps, b.AverageFps):+0.0;-0.0}%).";
        }
    }

    // ─── Export ─────────────────────────────────────────────────────
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (SelectedRun?.FrametimesMs == null || SelectedRun.FrametimesMs.Length == 0)
        {
            StatusText = "Open a run in Runs first.";
            return;
        }
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeChoices.Add("CSV", new[] { ".csv" });
            picker.SuggestedFileName = $"{SelectedRun.Game}-{SelectedRun.Date:yyyyMMdd-HHmmss}";
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            var sb = new StringBuilder("frame,frametime_ms,fps\n");
            for (int i = 0; i < SelectedRun.FrametimesMs.Length; i++)
                sb.Append(i).Append(',').Append(SelectedRun.FrametimesMs[i].ToString("F3"))
                  .Append(',').Append((1000.0 / SelectedRun.FrametimesMs[i]).ToString("F1")).Append('\n');
            await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
            StatusText = $"Exported {SelectedRun.FrametimesMs.Length} frames to CSV.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (SelectedRun == null || SelectedStats == null)
        {
            StatusText = "Open a run in Runs first.";
            return;
        }
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });
            picker.SuggestedFileName = $"{SelectedRun.Game}-{SelectedRun.Date:yyyyMMdd-HHmmss}";
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            var s = SelectedStats;
            var doc = new
            {
                game = SelectedRun.Game,
                exe = SelectedRun.Exe,
                date = SelectedRun.Date,
                durationSec = SelectedRun.DurationSec,
                comment = SelectedRun.Comment,
                systemInfo = SelectedRun.SystemInfo,
                stats = new
                {
                    frames = s.FrameCount,
                    avg = Math.Round(s.AverageFps, 1),
                    avgArithmetic = Math.Round(s.ArithmeticAvgFps, 1),
                    avgHarmonic = Math.Round(s.HarmonicAvgFps, 1),
                    median = Math.Round(s.MedianFps, 1),
                    p1 = Math.Round(s.P1Fps, 1),
                    p02 = Math.Round(s.P02Fps, 1),
                    low1Count = Math.Round(s.Low1CountFps, 1),
                    low01Count = Math.Round(s.Low01CountFps, 1),
                    low1Time = Math.Round(s.Low1TimeFps, 1),
                    low01Time = Math.Round(s.Low01TimeFps, 1),
                    min = Math.Round(s.MinFps, 1),
                    max = Math.Round(s.MaxFps, 1),
                    stutters = s.StutterCount,
                    sensorSummary = SensorSummaryText,
                },
            };
            await Windows.Storage.FileIO.WriteTextAsync(file,
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            StatusText = "Exported run report to JSON.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    // ─── Import ─────────────────────────────────────────────────────
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeFilter.Add(".csv");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            string text = await Windows.Storage.FileIO.ReadTextAsync(file);
            var groups = await Task.Run(() => BenchmarkImportService.ImportPresentMonCsv(
                text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None), file.Name));
            int saved = 0;
            foreach (var g in groups.Where(g => g.FrametimesMs.Length >= 60))
            {
                await _store.SaveAsync(new BenchmarkRun
                {
                    Game = g.Game,
                    Exe = g.Exe,
                    Date = g.Date,
                    DurationSec = g.FrametimesMs.Sum(f => (double)f) / 1000.0,
                    Comment = g.Comment,
                    ColorHex = PickRunColor(Runs.Count + saved),
                    SystemInfo = new BenchmarkSystemInfo(),
                    FrametimesMs = g.FrametimesMs,
                    TimestampsMs = g.TimestampsMs,
                });
                saved++;
            }
            await RefreshRunsAsync();
            StatusText = saved > 0
                ? $"Imported {saved} run(s) from {file.Name}."
                : "No process group had enough frames (min 60).";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            string text = await Windows.Storage.FileIO.ReadTextAsync(file);
            var groups = await Task.Run(() => BenchmarkImportService.ImportCapFrameXJson(text, file.Name));
            int ji = 0;
            foreach (var g in groups)
            {
                await _store.SaveAsync(new BenchmarkRun
                {
                    Game = g.Game,
                    Exe = g.Exe,
                    Date = g.Date,
                    DurationSec = g.FrametimesMs.Sum(f => (double)f) / 1000.0,
                    Comment = g.Comment,
                    ColorHex = PickRunColor(Runs.Count + ji++),
                    SystemInfo = new BenchmarkSystemInfo(),
                    FrametimesMs = g.FrametimesMs,
                    TimestampsMs = g.TimestampsMs,
                });
            }
            await RefreshRunsAsync();
            StatusText = $"Imported {groups.Count} run(s) from {file.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyStatsTable()
    {
        if (SelectedStats == null || SelectedRun == null)
        {
            StatusText = "Open a run first.";
            return;
        }
        var s = SelectedStats;
        string tsv = $"run\tavg\tP1\tP0.2\t1%low(n)\t1%low(t)\tmin\tmax\tstutters\n" +
                     $"{SelectedRun.Game}\t{s.AverageFps:F1}\t{s.P1Fps:F1}\t{s.P02Fps:F1}\t" +
                     $"{s.Low1CountFps:F1}\t{s.Low1TimeFps:F1}\t{s.MinFps:F1}\t{s.MaxFps:F1}\t{s.StutterCount}";
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(tsv);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            StatusText = "Stats table copied.";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    private static string FormatSensorSummary(SensorSummary s)
    {
        if (s == null) return "No sensor data.";
        string part(string name, SensorChannelSummary c, string unit) =>
            c.HasData ? $"{name} {c.Avg:F0}{unit} ({c.Min:F0}–{c.Max:F0})" : $"{name} n/a";
        string text = part("GPU", s.GpuTempC, "°") + " · "
                    + part("power", s.GpuPowerW, "W") + " · "
                    + part("util", s.GpuUtilPct, "%") + " · "
                    + part("CPU", s.CpuPct, "%");
        if (s.ThrottleMarks > 0) text += $" · {s.ThrottleMarks} throttle mark(s) (amber ticks)";
        return text;
    }

    internal static string FindConflicts(IEnumerable<string> processNames)
    {
        string[] watched = new[]
        {
            "RTSS", "RivaTuner", "HWiNFO64", "HWiNFO32", "PresentMon",
            "CapFrameX", "FRAPS", "OCAT", "MangoHud",
        };
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in processNames)
        {
            string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4] : name;
            foreach (string w in watched)
                if (bare.Equals(w, StringComparison.OrdinalIgnoreCase)) hit.Add(bare);
        }
        // Our own bundled session is expected while capturing - not a conflict.
        return hit.Count == 0 ? string.Empty :
            "Overlapping capture/overlay tools running (may corrupt ETW data): "
            + string.Join(", ", hit.OrderBy(h => h))
            + ". Close them before capturing.";
    }

    internal static BenchmarkSystemInfo SnapshotSystemInfo()
    {
        var info = new BenchmarkSystemInfo();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false);
            info.CpuName = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { }
        try
        {
            var v = Environment.OSVersion.Version;
            info.WindowsBuild = $"{v.Major}.{v.Minor}.{v.Build}";
            info.CpuName = $"{info.CpuName} ({Environment.ProcessorCount} logical)".Trim();
        }
        catch { }
        try { info.GamingMode = App.Current.GamingMode.IsActive; } catch { }
        try
        {
            Guid scheme = Services.WindowsSettingsService.GetActiveScheme();
            if (scheme != Guid.Empty) info.PowerPlan = scheme.ToString("D");
        }
        catch { }
        // Gaming mode no longer partitions CPU Sets (Docs/GameMode.md), so new
        // runs always record "none". The field stays because runs already stored
        // carry "game CCX: N sets" and the run list still renders it.
        info.CpuSetsPartition = "none";
        return info;
    }
}
