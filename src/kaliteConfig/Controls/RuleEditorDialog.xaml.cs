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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace kaliteConfig.Controls;

public sealed partial class RuleEditorDialog : ContentDialog, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Draft under edit. Same instance for the dialog lifetime; LoadFrom copies into it.</summary>
    public TunerProfile Draft { get; } = new();

    private bool _priorityEnabled;
    public bool PriorityEnabled
    {
        get => _priorityEnabled;
        set { if (Set(ref _priorityEnabled, value)) UpdateSummary(); }
    }

    private string _priorityName = "Normal";
    public string PriorityName
    {
        get => _priorityName;
        set => Set(ref _priorityName, value);
    }

    private bool _boostEnabled;
    public bool BoostEnabled
    {
        get => _boostEnabled;
        set { if (Set(ref _boostEnabled, value)) UpdateSummary(); }
    }

    private string _boostName = "Enabled";
    public string BoostName
    {
        get => _boostName;
        set => Set(ref _boostName, value);
    }

    private bool _efficiencyEnabled;
    public bool EfficiencyEnabled
    {
        get => _efficiencyEnabled;
        set { if (Set(ref _efficiencyEnabled, value)) UpdateSummary(); }
    }

    private string _efficiencyName = "Disabled";
    public string EfficiencyName
    {
        get => _efficiencyName;
        set => Set(ref _efficiencyName, value);
    }

    // No longer branch on E-core availability: nothing is pinned to cores any
    // more, so the promise is the same on every CPU (Docs/GameMode.md).
    public string AutomaticGamingModeDescription =>
        "Boost this app + put busy background tasks in Efficiency Mode";

    public string AutomaticGamingModeTooltip =>
        "When this process launches, raise it to Above Normal and put CPU-hungry background processes in Efficiency Mode (EcoQoS). Everything is restored when the app exits.";

    private bool _gamingAutoChecked;
    /// <summary>Rule triggers automatic app-wide Gaming mode on process launch.</summary>
    public bool GamingAutoChecked
    {
        get => _gamingAutoChecked;
        set { if (Set(ref _gamingAutoChecked, value)) UpdateSummary(); }
    }

    private bool _affinityEnabled;
    public bool AffinityEnabled
    {
        get => _affinityEnabled;
        set { if (Set(ref _affinityEnabled, value)) UpdateSummary(); }
    }

    private bool _cpuSetsEnabled;
    public bool CpuSetsEnabled
    {
        get => _cpuSetsEnabled;
        set { if (Set(ref _cpuSetsEnabled, value)) UpdateSummary(); }
    }

    public List<string> ProcessPriorityNames { get; } = new() { "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime" };
    public List<string> ToggleNames { get; } = new() { "Enabled", "Disabled" };

    private ulong _pendingAffinityMask;
    private List<ulong> _pendingCpuSetIds = new();
    private int _cpuCount;

    // Thread-rules tuner state (mirrors ThreadListDialog, but edits the saved
    // TunerThreadRule instead of a live thread).
    private TunerThreadRule? _selectedRule;
    private bool _loadingRuleEditor;
    private int _ruleIdealCpu = -1;
    private const int RuleCpuBoxColumns = 8;

    public RuleEditorDialog()
    {
        this.InitializeComponent();
        try
        {
            _cpuCount = App.Current.ProcessTuning.LogicalProcessorCount();
        }
        catch
        {
            _cpuCount = Environment.ProcessorCount;
        }
        ulong all = _cpuCount >= 64 ? ulong.MaxValue : ((1UL << _cpuCount) - 1UL);
        _pendingAffinityMask = all;
        Draft.ThreadRules.CollectionChanged += (_, _) =>
        {
            UpdateSummary();
            RefreshThreadRulesView();
        };
        UpdateSummary();
    }

    public void LoadFrom(TunerProfile source, bool isNew)
    {
        Title = isNew ? "New rule" : "Edit rule";
        Draft.CopyFrom(source);
        PriorityEnabled = Draft.PriorityClass.HasValue;
        PriorityName = Draft.PriorityClass.HasValue ? PriorityClassToName(Draft.PriorityClass.Value) : "Normal";
        BoostEnabled = Draft.BoostEnabled.HasValue;
        BoostName = Draft.BoostEnabled.HasValue && !Draft.BoostEnabled.Value ? "Disabled" : "Enabled";
        EfficiencyEnabled = Draft.EfficiencyMode.HasValue;
        EfficiencyName = Draft.EfficiencyMode.HasValue && Draft.EfficiencyMode.Value ? "Enabled" : "Disabled";
        GamingAutoChecked = Draft.GamingModeAuto;
        AffinityEnabled = Draft.AffinityMask.HasValue;
        CpuSetsEnabled = Draft.CpuSetIds != null && Draft.CpuSetIds.Count > 0;
        _pendingAffinityMask = Draft.AffinityMask ?? AllMask();
        _pendingCpuSetIds = Draft.CpuSetIds == null ? new List<ulong>() : new List<ulong>(Draft.CpuSetIds);
        UpdateAffinitySummary();
        UpdateCpuSetsSummary();
        ShowTab("Settings");
        HideError();
        UpdateSummary();
        _selectedRule = null;
        _ruleIdealCpu = -1;
        RefreshThreadRulesView();
    }

    // ---- tabs ----

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            ShowTab(tag);
        }
    }

    private void ShowTab(string tag)
    {
        bool settings = tag != "Threads";
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        ThreadsPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        TabSettings.FontWeight = settings ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        TabThreads.FontWeight = settings ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;
        if (!settings)
        {
            RefreshThreadRulesView();
        }
    }

    // ---- target suggestions ----

    private void TargetBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        try
        {
            string q = sender.Text ?? string.Empty;
            var names = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => n!.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .Take(12)
                .ToList();
            sender.ItemsSource = names;
        }
        catch
        {
        }
    }

    private void TargetBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s)
        {
            sender.Text = s;
        }
    }

    // ---- thread rules: same two-pane tuner as the Process tab's Threads dialog ----

    private void ThreadRulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectRule(ThreadRulesList.SelectedItem as TunerThreadRule);
    }

    private void SelectRule(TunerThreadRule? rule)
    {
        _selectedRule = rule;
        _ruleIdealCpu = -1;
        RuleEditorStatus(null);
        bool has = rule != null;
        RuleNoSelectionText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        RuleEditorPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (rule == null) return;
        _loadingRuleEditor = true;
        try
        {
            RuleEditorTitle.Text = rule.TargetText;
            RuleEditorSub.Text = string.IsNullOrWhiteSpace(rule.StartAddress)
                ? (rule.MatchAllThreads ? "Matches all threads" : "Matches any thread")
                : rule.StartAddress;
            RulePriorityCurrent.Text = "Current: " + rule.PriorityText;
            SelectComboByTag(RulePriorityCombo, rule.Priority);
            RuleBoostToggle.IsOn = rule.BoostEnabled ?? false;
            RuleEcoToggle.IsOn = rule.EfficiencyMode ?? false;
            RuleAffinityCurrent.Text = "Current: " + DescribeRuleAffinity(rule.AffinityMask);
            BuildRuleAffinityBoxes(rule.AffinityMask ?? AllMask());
            _ruleIdealCpu = -1;
            if (rule.IdealGroup.HasValue && rule.IdealIndex.HasValue && rule.IdealGroup.Value == 0)
            {
                _ruleIdealCpu = rule.IdealIndex.Value;
                RuleIdealCurrent.Text = $"Current: group 0 CPU {_ruleIdealCpu}";
            }
            else if (rule.IdealGroup.HasValue || rule.IdealIndex.HasValue)
            {
                RuleIdealCurrent.Text = $"Current: group {rule.IdealGroup?.ToString() ?? "?"} CPU {rule.IdealIndex?.ToString() ?? "?"} - only processor group 0 is tuneable here, pick a CPU below.";
            }
            else
            {
                RuleIdealCurrent.Text = "Current: Windows chooses (no preference) - pick a CPU below.";
            }
            BuildRuleIdealBoxes();
            foreach (var box in RuleIdealCpuGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
                box.IsChecked = _ruleIdealCpu >= 0 && box.Tag is int t && t == _ruleIdealCpu;
        }
        finally { _loadingRuleEditor = false; }
    }

    private void RefreshThreadRulesView()
    {
        int n = Draft.ThreadRules.Count;
        ThreadRulesHeaderText.Text = n == 0
            ? "No thread rules"
            : n == 1 ? "1 thread rule - select one to tune it" : $"{n} thread rules - select one to tune it";
        // Drop a selection that no longer exists (e.g. after delete).
        if (_selectedRule != null && !Draft.ThreadRules.Contains(_selectedRule))
        {
            ThreadRulesList.SelectedItem = null;
            SelectRule(null);
        }
        else if (_selectedRule != null)
        {
            // Refresh the editor text for the selected rule (priority text may have changed).
            var keep = _selectedRule;
            SelectRule(null);
            ThreadRulesList.SelectedItem = keep;
            SelectRule(keep);
        }
        // Default selection: tune the first rule without requiring a click.
        if (_selectedRule == null && Draft.ThreadRules.Count > 0)
        {
            var first = Draft.ThreadRules[0];
            ThreadRulesList.SelectedItem = first;
            if (_selectedRule == null) SelectRule(first);
        }
    }

    private void BuildRuleAffinityBoxes(ulong mask)
    {
        RuleAffinityCpuGrid.Children.Clear();
        RuleAffinityCpuGrid.ColumnDefinitions.Clear();
        RuleAffinityCpuGrid.RowDefinitions.Clear();
        int count = Math.Min(_cpuCount, 64);
        for (int c = 0; c < RuleCpuBoxColumns; c++)
            RuleAffinityCpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (count + RuleCpuBoxColumns - 1) / RuleCpuBoxColumns; r++)
            RuleAffinityCpuGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < count; i++)
        {
            var box = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = i.ToString(),
                Width = 44,
                IsChecked = (mask & (1UL << i)) != 0,
                Tag = i,
            };
            Grid.SetColumn(box, i % RuleCpuBoxColumns);
            Grid.SetRow(box, i / RuleCpuBoxColumns);
            RuleAffinityCpuGrid.Children.Add(box);
        }
    }

    private void BuildRuleIdealBoxes()
    {
        RuleIdealCpuGrid.Children.Clear();
        RuleIdealCpuGrid.ColumnDefinitions.Clear();
        RuleIdealCpuGrid.RowDefinitions.Clear();
        int count = Math.Min(_cpuCount, 64);
        for (int c = 0; c < RuleCpuBoxColumns; c++)
            RuleIdealCpuGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (count + RuleCpuBoxColumns - 1) / RuleCpuBoxColumns; r++)
            RuleIdealCpuGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < count; i++)
        {
            int cpu = i;
            var box = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = cpu.ToString(),
                Width = 44,
                Tag = cpu,
            };
            box.Click += (_, _) =>
            {
                _ruleIdealCpu = cpu;
                foreach (var other in RuleIdealCpuGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
                    other.IsChecked = ReferenceEquals(other, box);
            };
            Grid.SetColumn(box, cpu % RuleCpuBoxColumns);
            Grid.SetRow(box, cpu / RuleCpuBoxColumns);
            RuleIdealCpuGrid.Children.Add(box);
        }
    }

    private ulong ReadRuleAffinityMask()
    {
        ulong mask = 0;
        foreach (var box in RuleAffinityCpuGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
        {
            if (box.IsChecked == true && box.Tag is int cpu && cpu < 64)
                mask |= 1UL << cpu;
        }
        return mask;
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

    private string DescribeRuleAffinity(ulong? mask)
    {
        if (!mask.HasValue) return "All logical processors";
        ulong m = mask.Value;
        if (m == AllMask()) return "All logical processors";
        var cpus = new List<string>();
        for (int i = 0; i < _cpuCount && i < 64; i++)
            if ((m & (1UL << i)) != 0) cpus.Add(i.ToString());
        return cpus.Count == 0 ? "None" : $"Group 0: CPU {string.Join(", ", cpus)} (0x{m:X})";
    }

    private void RulePriorityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRuleEditor || _selectedRule == null) return;
        var level = ComboLevel(RulePriorityCombo);
        if (level == null) return;
        _selectedRule.Priority = level.Value;
        RulePriorityCurrent.Text = "Current: " + _selectedRule.PriorityText;
        Draft.RefreshSummaries();
        UpdateSummary();
        RuleEditorStatus(null);
    }

    private void RuleBoostBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not TunerThreadRule rule) return;
        bool want = cb.IsChecked == true;
        rule.BoostEnabled = want;
        if (_selectedRule == rule && !_loadingRuleEditor)
        {
            _loadingRuleEditor = true;
            try { RuleBoostToggle.IsOn = want; }
            finally { _loadingRuleEditor = false; }
        }
        Draft.RefreshSummaries();
        UpdateSummary();
    }

    private void RuleBoostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingRuleEditor || _selectedRule == null) return;
        _selectedRule.BoostEnabled = RuleBoostToggle.IsOn;
        Draft.RefreshSummaries();
        UpdateSummary();
        RuleEditorStatus(null);
    }

    private void RuleEcoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingRuleEditor || _selectedRule == null) return;
        _selectedRule.EfficiencyMode = RuleEcoToggle.IsOn;
        Draft.RefreshSummaries();
        UpdateSummary();
        RuleEditorStatus(null);
    }

    private void RuleAffinityApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule == null) return;
        ulong mask = ReadRuleAffinityMask();
        if (mask == 0)
        {
            RuleEditorStatus("Affinity: tick at least one CPU.", true);
            return;
        }
        if (mask == AllMask())
        {
            _selectedRule.AffinityMask = null;
            _selectedRule.AffinityGroup = null;
        }
        else
        {
            _selectedRule.AffinityGroup = 0;
            _selectedRule.AffinityMask = mask;
        }
        RuleAffinityCurrent.Text = "Current: " + DescribeRuleAffinity(_selectedRule.AffinityMask);
        Draft.RefreshSummaries();
        UpdateSummary();
        RuleEditorStatus(null);
    }

    private void RuleIdealApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule == null) return;
        if (_ruleIdealCpu < 0)
        {
            RuleEditorStatus("Ideal processor: pick one CPU box first.", true);
            return;
        }
        _selectedRule.IdealGroup = 0;
        _selectedRule.IdealIndex = (byte)_ruleIdealCpu;
        RuleIdealCurrent.Text = $"Current: group 0 CPU {_ruleIdealCpu}";
        Draft.RefreshSummaries();
        UpdateSummary();
        RuleEditorStatus(null);
    }

    private void ThreadRule_DeleteFlyout_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TunerThreadRule rule)
        {
            Draft.ThreadRules.Remove(rule);
            Draft.RefreshSummaries();
        }
    }

    private void ThreadRule_DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var rule = _selectedRule
            ?? (sender as FrameworkElement)?.DataContext as TunerThreadRule
            ?? ThreadRulesList.SelectedItem as TunerThreadRule;
        if (rule == null)
        {
            RuleEditorStatus("Select a thread rule first.", true);
            return;
        }
        Draft.ThreadRules.Remove(rule);
        Draft.RefreshSummaries();
    }

    private void RuleEditorStatus(string? message)
    {
        RuleEditorStatus(message, false);
    }

    private void RuleEditorStatus(string? message, bool warn)
    {
        if (string.IsNullOrEmpty(message))
        {
            RuleEditorStatusBox.Visibility = Visibility.Collapsed;
            RuleEditorStatusBox.Text = string.Empty;
        }
        else
        {
            RuleEditorStatusBox.Text = message;
            RuleEditorStatusBox.Visibility = Visibility.Visible;
        }
    }

    // ---- affinity / cpu sets choosers (flyouts; nested dialogs are not allowed) ----

    private ulong AllMask() => _cpuCount >= 64 ? ulong.MaxValue : ((1UL << _cpuCount) - 1UL);

    private void AffinityChoose_Click(object sender, RoutedEventArgs e)
    {
        _ = CpuPickerFlyouts.ShowAffinityPickerAsync(
            AffinityChooseButton,
            _cpuCount,
            _pendingAffinityMask,
            mask =>
            {
                _pendingAffinityMask = mask;
                AffinityEnabled = true;
                UpdateAffinitySummary();
                UpdateSummary();
            });
    }

    private void CpuSetsChoose_Click(object sender, RoutedEventArgs e)
    {
        _ = CpuPickerFlyouts.ShowCpuSetsPickerAsync(
            CpuSetsChooseButton,
            _pendingCpuSetIds,
            (ids, total) =>
            {
                _pendingCpuSetIds = ids;
                CpuSetsEnabled = true;
                UpdateCpuSetsSummary(total);
                UpdateSummary();
            });
    }

    private void UpdateAffinitySummary()
    {
        if (_pendingAffinityMask == AllMask())
        {
            DraftAffinitySummaryText.Text = "All logical processors";
        }
        else
        {
            int n = 0;
            for (int i = 0; i < _cpuCount && i < 64; i++)
            {
                if ((_pendingAffinityMask & (1UL << i)) != 0) n++;
            }
            DraftAffinitySummaryText.Text = $"{n} of {_cpuCount} · 0x{_pendingAffinityMask:X}";
        }
    }

    private void UpdateCpuSetsSummary(int total = -1)
    {
        if (_pendingCpuSetIds.Count == 0 || (total >= 0 && _pendingCpuSetIds.Count >= total))
        {
            CpuSetsSummaryText.Text = "All logical processors";
        }
        else
        {
            CpuSetsSummaryText.Text = $"{_pendingCpuSetIds.Count} selected";
        }
    }

    // ---- summary / validation ----

    private int EnabledActionCount =>
        (PriorityEnabled ? 1 : 0) +
        (BoostEnabled ? 1 : 0) +
        (EfficiencyEnabled ? 1 : 0) +
        (AffinityEnabled ? 1 : 0) +
        (CpuSetsEnabled ? 1 : 0);

    private void UpdateSummary()
    {
        int a = EnabledActionCount;
        int t = Draft.ThreadRules.Count;
        SummaryText.Text = $"{a} process setting{(a == 1 ? "" : "s")} · {t} thread rule{(t == 1 ? "" : "s")}";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private static uint PriorityNameToValue(string name) => name switch
    {
        "Idle" => 0x40,
        "BelowNormal" => 0x4000,
        "Normal" => 0x20,
        "AboveNormal" => 0x8000,
        "High" => 0x80,
        "Realtime" => 0x100,
        _ => 0x20,
    };

    private static string PriorityClassToName(uint value) => value switch
    {
        0x40 => "Idle",
        0x4000 => "BelowNormal",
        0x20 => "Normal",
        0x8000 => "AboveNormal",
        0x80 => "High",
        0x100 => "Realtime",
        _ => "Normal",
    };

    /// <summary>True when the dialog was accepted via the Ctrl+Enter shortcut (Hide carries no result).</summary>
    public bool ShortcutAccepted { get; private set; }

    private void Dialog_PrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!TryCommit())
        {
            args.Cancel = true;
        }
    }

    /// <summary>Validates and pushes editor state into <see cref="Draft"/>.</summary>
    private bool TryCommit()
    {
        HideError();
        string target = (TargetBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(target))
        {
            ShowError("Enter a target process name or pattern.");
            return false;
        }
        if (EnabledActionCount == 0 && !Draft.GamingModeAuto && Draft.ThreadRules.Count == 0)
        {
            ShowError("Enable at least one process setting or add one thread rule.");
            return false;
        }

        Draft.Name = target.Trim('*');
        if (string.IsNullOrEmpty(Draft.Name)) Draft.Name = target;
        Draft.Pattern = target;
        Draft.PriorityClass = PriorityEnabled ? PriorityNameToValue(PriorityName) : null;
        Draft.BoostEnabled = BoostEnabled ? BoostName == "Enabled" : null;
        Draft.EfficiencyMode = EfficiencyEnabled ? EfficiencyName == "Enabled" : null;
        Draft.GamingModeAuto = GamingAutoChecked;
        Draft.AffinityMask = AffinityEnabled ? _pendingAffinityMask : null;
        Draft.CpuSetIds = CpuSetsEnabled && _pendingCpuSetIds.Count > 0 ? new List<ulong>(_pendingCpuSetIds) : null;
        Draft.RefreshSummaries();
        return true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            if (TryCommit())
            {
                ShortcutAccepted = true;
                Hide();
            }
            e.Handled = true;
        }
    }
}
