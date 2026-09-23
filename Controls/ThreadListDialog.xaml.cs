using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using kaliteConfig.Native;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.Controls;

public sealed partial class ThreadRow : ObservableObject
{
    [ObservableProperty]
    public partial int Tid { get; set; }
    [ObservableProperty]
    public partial string CurrentText { get; set; } = "-";
    [ObservableProperty]
    public partial int CurrentLevel { get; set; } = int.MinValue;
    [ObservableProperty]
    public partial int Base { get; set; }
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsNamed { get; set; }
    [ObservableProperty]
    public partial string StartAddress { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool CanEdit { get; set; } = true;
    [ObservableProperty]
    public partial bool Suspended { get; set; }
    /// <summary>Per-row Priority-boost tick. Checked = boost allowed (default);
    /// unticked = boost forced off live AND persisted so it stays off for this
    /// thread identity across every future process launch. Backed by
    /// BoostPreferenceService - never a rule, never touches priority.</summary>
    [ObservableProperty]
    public partial bool BoostAllowed { get; set; } = true;
    public string ProcessName { get; set; } = string.Empty;
    public string StartAddressValue { get; set; } = string.Empty;
}

public sealed partial class ThreadListDialog : ContentDialog
{
    public ObservableCollection<ThreadRow> Rows { get; } = new();

    private int _pid;
    private string _processName = string.Empty;
    private ThreadRow? _selected;
    private bool _loadingEditor;
    private bool _endArmed;
    private bool _boostReadable = true;
    private bool _ecoReadable = true;
    private bool _idealUserPicked;
    private int _idealCpu = -1;
    private const int CpuBoxColumns = 8;

    private Services.ThreadTuningService Tuner => App.Current.ThreadTuning;

    public ThreadListDialog()
    {
        InitializeComponent();
        ThreadList.ItemsSource = Rows;
    }

    public async Task ShowForProcessAsync(int pid, string processName, XamlRoot root)
    {
        _pid = pid;
        _processName = processName;
        Title = $"Threads - {processName} ({pid})";
        XamlRoot = root;
        _ = ShowAsync();
        await LoadThreadsAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadThreadsAsync();

    private async Task LoadThreadsAsync()
    {
        Rows.Clear();
        SelectRow(null);
        EditorStatus("Loading threads…", false);
        try
        {
            var threads = await Services.ThreadQueryService.ListThreadsAsync(_pid);
            bool anyLocked = false;
            // Named threads pin to the top (then TID order); unnamed follow.
            foreach (var t in threads
                .OrderByDescending(t => !string.IsNullOrWhiteSpace(t.Description) && t.Description != "(unnamed)")
                .ThenBy(t => t.Tid))
            {
                bool named = !string.IsNullOrWhiteSpace(t.Description) && t.Description != "(unnamed)";
                var row = new ThreadRow
                {
                    Tid = t.Tid,
                    Base = t.Base,
                    Description = t.Description,
                    StartAddress = t.StartAddress,
                    CurrentText = t.RelativeText,
                    IsNamed = named,
                    ProcessName = _processName,
                    // Seeded from the live boost state below; the tick box is
                    // the single source of truth once loaded.
                    BoostAllowed = true,
                };
                try
                {
                    using var h = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.QueryInformation, false, (uint)t.Tid);
                    if (h.IsInvalid) throw new UnauthorizedAccessException();
                    int level = NativeMethods.Priority.GetThreadPriority(h);
                    row.CurrentLevel = level;
                    row.CurrentText = DescribePriority(level);
                }
                catch
                {
                    row.CanEdit = false;
                    anyLocked = true;
                }
                Rows.Add(row);
            }
            HeaderText.Text = $"{Rows.Count} threads - select one to tune it";
            if (anyLocked)
                EditorStatus("Some threads are protected - Windows blocks tuning them.", true);
            else
                EditorStatus(null, false);
            if (Rows.Count == 0)
                HeaderText.Text = "No threads readable (process may have exited or access was denied).";
        }
        catch (Exception ex)
        {
            HeaderText.Text = "Failed to read threads.";
            EditorStatus(ex.Message, true);
        }
    }

    private async void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try { await SelectRowAsync(ThreadList.SelectedItem as ThreadRow); }
        catch (Exception ex) { try { EditorStatus($"Could not inspect thread: {Short(ex)}", true); } catch { } }
    }

    private void SelectRow(ThreadRow? row)
    {
        _selected = row;
        _endArmed = false;
        _idealUserPicked = false;
        EndButton.Content = "End thread";
        bool has = row != null;
        NoSelectionText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        EditorPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SelectRowAsync(ThreadRow? row)
    {
        try
        {
            await SelectRowInnerAsync(row);
        }
        catch (Exception ex)
        {
            try { _loadingEditor = false; EditorStatus($"Could not inspect thread: {Short(ex)}", true); }
            catch { }
        }
    }

    private async Task SelectRowInnerAsync(ThreadRow? row)
    {
        SelectRow(row);
        EditorStatus(null, false);
        if (row == null) return;
        _loadingEditor = true;
        try
        {
            EditorTitle.Text = $"TID {row.Tid}";
            EditorSub.Text = $"{row.Description} - {row.StartAddress}";
            PriorityCurrent.Text = "Current: " + row.CurrentText;
            SelectComboByTag(PriorityCombo, row.CurrentLevel);
            // Live tunables; each degrades independently.
            _boostReadable = true;
            _ecoReadable = true;
            try { row.BoostAllowed = await Tuner.GetBoostAsync((uint)row.Tid); }
            catch { _boostReadable = false; }
            try { EcoToggle.IsOn = await Tuner.GetEfficiencyAsync((uint)row.Tid); }
            catch { EcoToggle.IsEnabled = false; _ecoReadable = false; }
            try
            {
                AffinityCurrent.Text = "Current: " + await Tuner.GetAffinityDescriptionAsync((uint)row.Tid);
                var (g, m) = await Tuner.GetAffinityStateAsync((uint)row.Tid);
                BuildAffinityBoxes(m);
            }
            catch (Exception ex)
            {
                AffinityCurrent.Text = "Current: unreadable (" + Short(ex) + ")";
                BuildAffinityBoxes(0);
            }
            _idealCpu = -1;
            IdealCurrent.Text = string.Empty;
            BuildIdealBoxes();
            try
            {
                var (ig, cpu) = await Tuner.GetIdealProcessorAsync((uint)row.Tid);
                IdealCurrent.Text = $"Current: group {ig} CPU {cpu}";
                // Only tick a box when the thread really lives in group 0 and
                // the read succeeded - a failed read must leave nothing
                // pre-selected, or "Apply" silently re-applies CPU 0.
                if (ig == 0)
                {
                    _idealCpu = cpu;
                    foreach (var box in IdealCpuGrid.Children.OfType<ToggleButton>())
                        box.IsChecked = box.Tag is int t && t == cpu;
                }
                else
                {
                    IdealCurrent.Text += " - only processor group 0 is tuneable here, pick a CPU below.";
                }
            }
            catch { IdealCurrent.Text = "Current: unreadable - pick a CPU below."; }
            SuspendButton.Content = row.Suspended ? "Resume" : "Suspend";
            bool editable = row.CanEdit;
            PriorityCombo.IsEnabled = editable;
            EcoToggle.IsEnabled = editable && _ecoReadable;
            AffinityCpuGrid.Opacity = editable ? 1 : 0.5;
            IdealCpuGrid.Opacity = editable ? 1 : 0.5;
            SetCpuBoxesEnabled(AffinityCpuGrid, editable);
            SetCpuBoxesEnabled(IdealCpuGrid, editable);
            SuspendButton.IsEnabled = editable;
            EndButton.IsEnabled = editable;
            if (!editable)
                EditorStatus("Protected thread - Windows does not allow changes.", true);
        }
        finally { _loadingEditor = false; }
    }

    /// <summary>Builds one toggle box per logical CPU. Affinity boxes are
    /// multi-select (checked = allowed); ideal boxes are single-select.</summary>
    private void BuildAffinityBoxes(ulong liveMask)
    {
        AffinityCpuGrid.Children.Clear();
        AffinityCpuGrid.ColumnDefinitions.Clear();
        AffinityCpuGrid.RowDefinitions.Clear();
        int count = Math.Min(Environment.ProcessorCount, 64);
        for (int c = 0; c < CpuBoxColumns; c++)
            AffinityCpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (count + CpuBoxColumns - 1) / CpuBoxColumns; r++)
            AffinityCpuGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < count; i++)
        {
            int cpu = i;
            var box = new ToggleButton
            {
                Content = cpu.ToString(),
                Width = 44,
                IsChecked = (liveMask & (1UL << cpu)) != 0,
                Tag = cpu,
            };
            Grid.SetColumn(box, cpu % CpuBoxColumns);
            Grid.SetRow(box, cpu / CpuBoxColumns);
            AffinityCpuGrid.Children.Add(box);
        }
    }

    private void BuildIdealBoxes()
    {
        IdealCpuGrid.Children.Clear();
        IdealCpuGrid.ColumnDefinitions.Clear();
        IdealCpuGrid.RowDefinitions.Clear();
        int count = Math.Min(Environment.ProcessorCount, 64);
        for (int c = 0; c < CpuBoxColumns; c++)
            IdealCpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (count + CpuBoxColumns - 1) / CpuBoxColumns; r++)
            IdealCpuGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < count; i++)
        {
            int cpu = i;
            var box = new ToggleButton
            {
                Content = cpu.ToString(),
                Width = 44,
                Tag = cpu,
            };
            box.Click += (_, _) =>
            {
                _idealCpu = cpu;
                _idealUserPicked = true;
                foreach (var other in IdealCpuGrid.Children.OfType<ToggleButton>())
                    other.IsChecked = ReferenceEquals(other, box);
            };
            Grid.SetColumn(box, cpu % CpuBoxColumns);
            Grid.SetRow(box, cpu / CpuBoxColumns);
            IdealCpuGrid.Children.Add(box);
        }
    }

    private ulong ReadAffinityMask()
    {
        ulong mask = 0;
        foreach (var box in AffinityCpuGrid.Children.OfType<ToggleButton>())
        {
            if (box.IsChecked == true && box.Tag is int cpu && cpu < 64)
                mask |= 1UL << cpu;
        }
        return mask;
    }

    private static void SetCpuBoxesEnabled(Grid grid, bool enabled)
    {
        foreach (var box in grid.Children.OfType<ToggleButton>())
            box.IsEnabled = enabled;
    }

    private static void SelectComboByTag(ComboBox box, int level)
    {
        box.SelectedItem = null;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string s && int.TryParse(s, out int v) && v == level)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static int? ComboLevel(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item && item.Tag is string s && int.TryParse(s, out int v))
            return v;
        return null;
    }

    private async void PriorityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || _selected == null) return;
        var level = ComboLevel(PriorityCombo);
        if (level == null) return;
        try
        {
            await Tuner.SetPriorityAsync((uint)_selected.Tid, level.Value);
            _selected.CurrentLevel = level.Value;
            _selected.CurrentText = DescribePriority(level.Value);
            PriorityCurrent.Text = "Current: " + _selected.CurrentText;
            EditorStatus(null, false);
        }
        catch (Exception ex) { EditorStatus($"Priority: {Short(ex)}", true); }
    }

    private async void PermanentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var row = _selected;
        try
        {
            int? prio = ComboLevel(PriorityCombo) ?? (row.CurrentLevel != int.MinValue ? row.CurrentLevel : null);
            if (prio == null)
            {
                EditorStatus("Cannot read the live priority - pick one from the dropdown first.", true);
                return;
            }
            var rule = new Models.TunerThreadRule
            {
                Description = row.Description == "(unnamed)" ? string.Empty : row.Description,
                StartAddress = row.StartAddress == "-" ? string.Empty : row.StartAddress,
                Priority = prio.Value,
                BoostEnabled = _boostReadable ? row.BoostAllowed : null,
                EfficiencyMode = _ecoReadable ? EcoToggle.IsOn : null,
            };
            // One-click save: take affinity + ideal straight from the editor
            // UI so the user does NOT have to press "Apply affinity" / "Apply
            // ideal processor" first. The boxes are seeded from the live
            // thread, so an untouched editor still saves the live state.
            ulong uiMask = ReadAffinityMask();
            if (uiMask != 0)
            {
                rule.AffinityGroup = 0;
                rule.AffinityMask = uiMask;
            }
            if (_idealUserPicked && _idealCpu >= 0 && _idealCpu < 64)
            {
                rule.IdealGroup = 0;
                rule.IdealIndex = (byte)_idealCpu;
            }
            // A rule with neither description nor address matches nothing
            // (ThreadMatches requires one) - mark it match-all so an unnamed
            // thread rule really applies to every thread, as the status line claims.
            if (string.IsNullOrWhiteSpace(rule.Description) && string.IsNullOrWhiteSpace(rule.StartAddress))
                rule.MatchAllThreads = true;
            var watcher = App.Current.ProfileWatcher;
            var existing = watcher.ActiveProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, _processName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Pattern, _processName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new Models.TunerProfile
                {
                    Name = _processName,
                    Pattern = _processName,
                    AutoApply = true,
                    Enabled = true,
                };
            }
            existing.ThreadRules.Add(rule);
            existing.RefreshSummaries();
            await watcher.AddOrUpdate(existing);

            // "Make permanent" must also mean "in effect now": apply the freshly
            // saved rule to THIS running instance immediately instead of waiting
            // for the process to be launched again - a rule used to sit idle until
            // then, which read as "my changes keep resetting".
            int applied = 0;
            try
            {
                var result = await watcher.ApplyToProcessAsync(existing, _pid);
                applied = result.Succeeded;
            }
            catch { }

            bool wide = string.IsNullOrEmpty(rule.Description) && string.IsNullOrEmpty(rule.StartAddress);
            EditorStatus(wide
                ? $"Saved - applies to ALL threads of {_processName} ({applied} thread(s) now) and on every launch."
                : $"Saved - applied to {applied} matching thread(s) of {_processName} now, and on every launch.", false);
        }
        catch (Exception ex) { EditorStatus($"Save rule: {Short(ex)}", true); }
    }

    private async void BoostBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThreadRow row }) return;
        try
        {
            await Services.BoostPreferenceService.Instance.RestoreAsync(
                row.ProcessName, row.Tid, row.Description, row.StartAddress);
            EditorStatus(null, false);
        }
        catch (Exception ex)
        {
            row.BoostAllowed = false; // revert the tick to reflect reality
            EditorStatus($"Boost: {Short(ex)}", true);
        }
    }

    private async void BoostBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThreadRow row }) return;
        try
        {
            await Services.BoostPreferenceService.Instance.SuppressAsync(
                row.ProcessName, row.Tid, row.Description, row.StartAddress);
            EditorStatus("Boost off - stays off for this thread across restarts.", false);
        }
        catch (Exception ex)
        {
            row.BoostAllowed = true;
            EditorStatus($"Boost: {Short(ex)}", true);
        }
    }

    private async void EcoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _selected == null) return;
        try
        {
            await Tuner.SetEfficiencyAsync((uint)_selected.Tid, EcoToggle.IsOn);
            EditorStatus(null, false);
        }
        catch (Exception ex) { EditorStatus($"Efficiency mode: {Short(ex)}", true); }
    }

    private async void AffinityApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        try
        {
            ulong mask = ReadAffinityMask();
            if (mask == 0)
            {
                EditorStatus("Affinity: tick at least one CPU.", true);
                return;
            }
            await Tuner.SetAffinityAsync((uint)_selected.Tid, null, mask);
            AffinityCurrent.Text = "Current: " + await Tuner.GetAffinityDescriptionAsync((uint)_selected.Tid);
            EditorStatus(null, false);
        }
        catch (Exception ex) { EditorStatus($"Affinity: {Short(ex)}", true); }
    }

    private async void IdealApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (_idealCpu < 0)
        {
            EditorStatus("Ideal processor: pick one CPU box first.", true);
            return;
        }
        try
        {
            // The service verifies the write against the kernel (and explains
            // a rejection, e.g. a CPU outside this thread's affinity mask).
            await Tuner.SetIdealProcessorAsync((uint)_selected.Tid, 0, (byte)_idealCpu);
            _idealUserPicked = true;
            EditorStatus($"Ideal processor set to CPU {_idealCpu}.", false);
            // Display what Windows reports, not what we asked for.
            try
            {
                var (ig, cpu) = await Tuner.GetIdealProcessorAsync((uint)_selected.Tid);
                IdealCurrent.Text = $"Current: group {ig} CPU {cpu}";
            }
            catch { IdealCurrent.Text = $"Current: group 0 CPU {_idealCpu}."; }
        }
        catch (Exception ex) { EditorStatus($"Ideal processor: {Short(ex)}", true); }
    }

    private async void SuspendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        try
        {
            if (_selected.Suspended)
            {
                await Tuner.ResumeThreadAsync((uint)_selected.Tid);
                _selected.Suspended = false;
                SuspendButton.Content = "Suspend";
            }
            else
            {
                await Tuner.SuspendThreadAsync((uint)_selected.Tid);
                _selected.Suspended = true;
                SuspendButton.Content = "Resume";
            }
            EditorStatus(null, false);
        }
        catch (Exception ex) { EditorStatus($"Suspend/resume: {Short(ex)}", true); }
    }

    private async void EndButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (!_endArmed)
        {
            _endArmed = true;
            EndButton.Content = "Click again to confirm kill";
            EditorStatus("Ending a thread can crash the process. Click again to confirm.", true);
            return;
        }
        try
        {
            await Tuner.TerminateThreadAsync((uint)_selected.Tid);
            EditorStatus($"TID {_selected.Tid} terminated - refreshing list.", false);
            await LoadThreadsAsync();
        }
        catch (Exception ex) { EditorStatus($"End thread: {Short(ex)}", true); }
        finally { _endArmed = false; EndButton.Content = "End thread"; }
    }

    private void EditorStatus(string? message, bool warn)
    {
        if (string.IsNullOrEmpty(message))
        {
            EditorStatusBox.Visibility = Visibility.Collapsed;
            EditorStatusBox.Text = string.Empty;
        }
        else
        {
            EditorStatusBox.Text = message;
            EditorStatusBox.Visibility = Visibility.Visible;
        }
    }

    // Priority levels match System.Threading.ThreadPriorityLevel (Idle -15 …
    // Highest +2) plus Win32 TIME_CRITICAL (+15); see SetThreadPriority docs.
    private static string DescribePriority(int level) => level switch
    {
        -15 => "Idle (-15)",
        -2 => "Lowest (-2)",
        -1 => "Below normal (-1)",
        0 => "Normal (0)",
        1 => "Above normal (+1)",
        2 => "Highest (+2)",
        15 => "Time critical (+15)",
        > 2 => $"High ({level})",
        _ => $"Level {level}",
    };

    private static string Short(Exception ex) =>
        ex is UnauthorizedAccessException
            ? "Access denied - protected thread."
            : ex.Message.Split('\n')[0];
}
