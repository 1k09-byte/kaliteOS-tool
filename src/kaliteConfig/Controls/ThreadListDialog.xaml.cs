// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using kaliteConfig.Native;
using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Which logical CPUs this thread is allowed to run on, e.g. <c>0-7</c>,
    /// <c>all 16</c>, or <c>0,2,4</c>. Empty when it could not be read.
    /// </summary>
    [ObservableProperty] public partial string CpuText { get; set; } = "-";

    /// <summary>
    /// The thread's preferred CPU, or null when it has no preference and the
    /// scheduler is free to choose from the affinity mask.
    /// </summary>
    [ObservableProperty] public partial string? IdealCpuText { get; set; }

    /// <summary>How much CPU time this one thread has used, in seconds.</summary>
    [ObservableProperty] public partial string CpuTimeText { get; set; } = "-";

    /// <summary>
    /// The thread's share of the parent's total CPU time, 0-100. Null when it
    /// cannot be computed (the parent total read failed or is still zero).
    /// </summary>
    [ObservableProperty] public partial double? CpuShare { get; set; }

    /// <summary>
}

public sealed partial class ThreadListDialog : ContentDialog
{
    public ObservableCollection<ThreadRow> Rows { get; } = new();

    private int _pid;
    private string _processName = string.Empty;
    private ThreadRow? _selected;
    private bool _loadingEditor;
    /// <summary>
    /// True while the thread LIST is being rebuilt. The row template binds a
    /// two-way CheckBox to <see cref="ThreadRow.BoostAllowed"/> and hooks
    /// Checked/Unchecked, so every recycled container whose value differs from
    /// the row it is being re-bound to fires those handlers. Without this guard
    /// a plain "Refresh list" wrote boost state to live threads it never
    /// touched - the list rebuild was silently mutating the system.
    /// </summary>
    private bool _loadingList;
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

    /// <summary>
    /// Fills in the per-row CPU placement: the affinity mask as readable CPU
    /// numbers, and the thread's preferred (ideal) processor.
    ///
    /// Each read is independent and individually guarded, because a process can
    /// allow a priority query while refusing an affinity one - a protected or
    /// cross-session thread returns failure rather than faulting the whole list.
    /// </summary>
    private void ApplyCpuInfo(ThreadRow row, kaliteConfig.Native.SafeThreadHandle thread)
    {
        try
        {
            if (NativeMethods.Affinity.GetThreadGroupAffinity(thread, out var affinity))
            {
                int coreCount = Math.Max(1, Environment.ProcessorCount);
                if (affinity.Mask == 0)
                {
                    // Zero is not a real mask; it means the thread is free to run
                    // anywhere, which is what an all-ones mask means in practice.
                    row.CpuText = $"all {coreCount}";
                }
                else
                {
                    var parts = new List<string>();
                    for (int i = 0; i < 64; i++)
                        if ((affinity.Mask & (1UL << i)) != 0) parts.Add(i.ToString());
                    row.CpuText = parts.Count > 0 ? string.Join(", ", parts) : "none";
                }
            }
            else row.CpuText = "unknown";
        }
        catch { row.CpuText = "unknown"; }

        try
        {
            if (NativeMethods.Affinity.GetThreadIdealProcessorEx(thread, out var ideal))
            {
                // Group 0xFF with number 0xFF is the documented "no preference"
                // sentinel; saying "CPU 255" would be actively misleading.
                row.IdealCpuText = (ideal.Group == 0xFF && ideal.Number == 0xFF)
                    ? "none (scheduler picks)"
                    : $"CPU {ideal.Number}" + (ideal.Group != 0 ? $" (group {ideal.Group})" : "");
            }
        }
        catch { row.IdealCpuText = null; }
    }

    private async Task LoadThreadsAsync()
    {
        // Suppress the row CheckBox handlers for the whole rebuild: Rows.Clear()
        // plus re-add recycles every container, and each rebind fires
        // Checked/Unchecked. See _loadingList.
        _loadingList = true;
        try
        {
            Rows.Clear();
            SelectRow(null);
            EditorStatus("Loading threads…", false);
            try
            {
                // One pass over the thread list for the CPU-time figures, rather
                // than per row: Process.Threads materialises every thread on each
                // access, so reading it inside the loop is quadratic and is
                // painful on a process with hundreds of threads.
                double parentCpuSeconds = 0;
                var cpuSecondsByTid = new Dictionary<int, double>();
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(_pid);
                    foreach (System.Diagnostics.ProcessThread pt in proc.Threads)
                    {
                        double s = pt.TotalProcessorTime.TotalSeconds;
                        cpuSecondsByTid[pt.Id] = s;
                        parentCpuSeconds += s;
                    }
                }
                catch { /* the per-row figures just stay blank */ }

                var threads = await Services.ThreadQueryService.ListThreadsAsync(_pid);
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
                    };

                    try { row.BoostAllowed = await Tuner.GetBoostAsync((uint)t.Tid); } catch { row.BoostAllowed = true; }
                    if (Services.BoostPreferenceService.Instance.IsSuppressed(_processName, t.Tid, t.Description, t.StartAddress))
                        row.BoostAllowed = false;
                    try
                    {
                        using var h = NativeMethods.Handles.OpenThread(
                            NativeMethods.ThreadAccess.QueryInformation, false, (uint)t.Tid);
                        if (h.IsInvalid) throw new UnauthorizedAccessException();
                        int level = NativeMethods.Priority.GetThreadPriority(h);
                        row.CurrentLevel = level;
                        row.CurrentText = DescribePriority(level);

                        // Which CPUs this thread may use, and which one it would
                        // prefer. Both are read here rather than in the editor so the
                        // list answers "what is running where" without a click.
                        ApplyCpuInfo(row, h);

                        if (cpuSecondsByTid.TryGetValue(t.Tid, out double secs))
                        {
                            row.CpuTimeText = secs < 60
                                ? $"{secs:0.0}s"
                                : secs < 3600 ? $"{secs / 60:0}m {secs % 60:0}s"
                                : $"{secs / 3600:0}h {(secs % 3600) / 60:0}m";
                            if (parentCpuSeconds > 0)
                                row.CpuShare = Math.Round(100.0 * secs / parentCpuSeconds, 1);
                        }
                    }
                    catch
                    {
                        row.CanEdit = false;
                    }
                    Rows.Add(row);
                }
                if (Rows.Count == 0)
                {
                    HeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    HeaderText.Text = "No threads readable (process may have exited or access was denied).";
                    Title = $"Threads - {_processName} ({_pid}) — 0 threads";
                }
                else
                {
                    HeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    Title = $"Threads - {_processName} ({_pid})  —  {Rows.Count} threads - select one to tune it";
                }
            }
            catch (Exception ex)
            {
                HeaderText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                HeaderText.Text = "Failed to read threads.";
                EditorStatus(ex.Message, true);
            }
        }
        finally
        {
            // Released only after the last Rows.Add has been pumped through the
            // binding, otherwise a late rebind would still slip past the guard.
            _loadingList = false;
        }

        // Default selection: tune the first thread without requiring a click.
        // Refresh clears the selection (Rows.Clear drops it), so this also
        // re-selects after Refresh list.
        if (ThreadList.SelectedItem == null && Rows.Count > 0)
            ThreadList.SelectedItem = Rows[0];
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
            
            if (Services.BoostPreferenceService.Instance.IsSuppressed(_processName, row.Tid, row.Description, row.StartAddress))
                row.BoostAllowed = false;

            BoostToggle.IsOn = row.BoostAllowed;
            BoostToggle.IsEnabled = row.CanEdit && _boostReadable;

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

    /// <summary>
    /// The row tick writes to the LIVE thread, so it must fire only on a real
    /// user click. Checked/Unchecked also fire when the two-way binding pushes a
    /// new value into a recycled container during a list rebuild - which is what
    /// made "Refresh list" silently rewrite boost state on threads the user never
    /// touched. Click is the one signal that only a real interaction produces.
    /// </summary>
    private async void BoostBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _loadingList) return;
        if (sender is not CheckBox cb || cb.DataContext is not ThreadRow row) return;

        bool want = cb.IsChecked == true;
        if (want == row.BoostAllowed) return; // binding caught up; nothing to do
        try
        {
            if (want)
                await Services.BoostPreferenceService.Instance.RestoreAsync(
                    _processName, row.Tid, row.Description, row.StartAddress);
            else
                await Services.BoostPreferenceService.Instance.SuppressAsync(
                    _processName, row.Tid, row.Description, row.StartAddress);
            row.BoostAllowed = want;
            if (_selected == row) BoostToggle.IsOn = want;
        }
        catch (Exception ex)
        {
            // Put the tick back - the write did not happen.
            row.BoostAllowed = !want;
            EditorStatus($"Boost: {Short(ex)}", true);
        }
    }
    private async void BoostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _selected == null) return;
        try
        {
            if (BoostToggle.IsOn)
            {
                await Services.BoostPreferenceService.Instance.RestoreAsync(
                    _selected.ProcessName, _selected.Tid, _selected.Description, _selected.StartAddress);
                EditorStatus(null, false);
            }
            else
            {
                await Services.BoostPreferenceService.Instance.SuppressAsync(
                    _selected.ProcessName, _selected.Tid, _selected.Description, _selected.StartAddress);
                EditorStatus("Boost off - stays off for this thread across restarts.", false);
            }
            _selected.BoostAllowed = BoostToggle.IsOn;
        }
        catch (Exception ex)
        {
            _loadingEditor = true;
            BoostToggle.IsOn = !BoostToggle.IsOn;
            _loadingEditor = false;
            
            _selected.BoostAllowed = BoostToggle.IsOn;
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
