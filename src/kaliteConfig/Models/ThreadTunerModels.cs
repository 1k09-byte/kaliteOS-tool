using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace kaliteConfig.Models;

public enum TunerPriorityClass : uint
{
    Idle = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
    Realtime = 0x100,
}

public enum TunerThreadPriority
{
    Idle = -15,
    Lowest = -2,
    BelowNormal = -1,
    Normal = 0,
    AboveNormal = 1,
    Highest = 2,
    TimeCritical = 15,
}

public partial class TunerProcessRow : ObservableObject
{
    [ObservableProperty] public partial int Pid { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Path { get; set; } = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppIconVis))]
    [NotifyPropertyChangedFor(nameof(GenericIconVis))]
    public partial Microsoft.UI.Xaml.Media.ImageSource? AppIcon { get; set; }

    public Microsoft.UI.Xaml.Visibility AppIconVis =>
        AppIcon == null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility GenericIconVis =>
        AppIcon == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty] public partial string PriorityText { get; set; } = "?";
    [ObservableProperty] public partial uint PriorityValue { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AffinityDetail))]
    public partial string AffinitySummary { get; set; } = "?";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AffinityDetail))]
    public partial ulong AffinityMask { get; set; }

    public string AffinityDetail =>
        AffinitySummary == "?"
            ? "Affinity unknown (access denied or not sampled yet)"
            : $"Affinity mask 0x{AffinityMask:X}";
    [ObservableProperty] public partial string PriorityBoostText { get; set; } = "Unknown";

    [ObservableProperty] public partial bool? BoostAllowed { get; set; }
    [ObservableProperty] public partial string Memory { get; set; } = "0 MB";
    [ObservableProperty] public partial int Threads { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "Running";
    [ObservableProperty] public partial long ContextSwitchesDelta { get; set; }
    [ObservableProperty] public partial long CyclesDelta { get; set; }
    [ObservableProperty] public partial string Rule { get; set; } = "None";

    [ObservableProperty] public partial int ParentPid { get; set; }

    [ObservableProperty] public partial int Depth { get; set; }

    [NotifyPropertyChangedFor(nameof(HasChildrenVis))]
    [ObservableProperty] public partial bool HasChildren { get; set; }

    public Microsoft.UI.Xaml.Visibility HasChildrenVis =>
        HasChildren ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [ObservableProperty] public partial string EfficiencyMode { get; set; } = "Disabled";
    [ObservableProperty] public partial string BoostText { get; set; } = "Unknown";
    [ObservableProperty] public partial bool EfficiencyModeBool { get; set; }
    [ObservableProperty] public partial string SettingsSummary { get; set; } = "Settings unavailable";
    [ObservableProperty] public partial double CpuPercent { get; set; }
    [ObservableProperty] public partial bool IsProtected { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = string.Empty;

    public Microsoft.UI.Xaml.Thickness IndentMargin =>
        new Microsoft.UI.Xaml.Thickness(Depth * 14.0, 0, 0, 0);

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    [NotifyPropertyChangedFor(nameof(ShowCountersVis))]
    [ObservableProperty] public partial bool ShowCounters { get; set; }

    public Microsoft.UI.Xaml.Visibility ShowCountersVis =>
        ShowCounters ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

public partial class ThreadBoostRow : ObservableObject
{
    [ObservableProperty] public partial int Tid { get; set; }
    [ObservableProperty] public partial int Pid { get; set; }
    [ObservableProperty] public partial string ProcessName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = "(unnamed)";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BoostDisplay))] public partial bool BoostEnabled { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BoostDisplay))] public partial bool IsBoostKnown { get; set; }
    public string BoostDisplay => !IsBoostKnown ? "Unknown" : (BoostEnabled ? "On" : "Off");
    [ObservableProperty] public partial bool IsProtected { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
}

public enum AffinityScope
{
    Unset,
    PerformanceCores,
    EfficiencyCores,
    SingleThreadPerCore,
    SharedCacheGroup,
    GameDefault,
}

public sealed partial class TunerThreadRule : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    [NotifyPropertyChangedFor(nameof(IsNamed))]
    [NotifyPropertyChangedFor(nameof(NamedVis))]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    [NotifyPropertyChangedFor(nameof(RuleIdText))]
    public partial string StartAddress { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleIdText))]
    public partial bool MatchAllThreads { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(PriorityText))]
    public partial int Priority { get; set; }

    [JsonIgnore]
    public string PriorityText => DescribePriority(Priority);

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial bool? EfficiencyMode { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial bool? BoostEnabled { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(AffinitySummaryText))]
    public partial ulong? AffinityMask { get; set; }
    [ObservableProperty]
    public partial ushort? AffinityGroup { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial ushort? IdealGroup { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial byte? IdealIndex { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial uint? MemoryPriority { get; set; }
    [ObservableProperty]
    public partial int TargetCount { get; set; }

    [JsonIgnore]
    public string TargetText =>
        string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(StartAddress)
            ? "(any thread)"
            : string.Join(" · ", new[] { Description, StartAddress }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [JsonIgnore]
    public bool IsNamed => !string.IsNullOrWhiteSpace(Description) && Description != "(unnamed)";

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility NamedVis =>
        IsNamed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [JsonIgnore]
    public string RuleIdText =>
        !string.IsNullOrWhiteSpace(StartAddress) ? $"Start {StartAddress}"
        : MatchAllThreads ? "Matches all threads"
        : "(any thread)";

    [JsonIgnore]
    public string AffinitySummaryText
    {
        get
        {
            if (!AffinityMask.HasValue) return "All logical processors";
            ulong mask = AffinityMask.Value;
            if (mask == ulong.MaxValue) return "All logical processors";
            int count = System.Numerics.BitOperations.PopCount(mask);
            return $"{count} pinned · 0x{mask:X}";
        }
    }

    [JsonIgnore]
    public string DetailText
    {
        get
        {
            var parts = new List<string> { $"Priority: {Priority}" };
            if (EfficiencyMode.HasValue) parts.Add(EfficiencyMode.Value ? "Eco ON" : "Eco OFF");
            if (BoostEnabled.HasValue) parts.Add(BoostEnabled.Value ? "Boost ON" : "Boost OFF");
            if (AffinityMask.HasValue) parts.Add($"Affinity 0x{AffinityMask.Value:X}");
            if (IdealGroup.HasValue && IdealIndex.HasValue) parts.Add($"Ideal G{IdealGroup.Value}:{IdealIndex.Value}");
            if (MemoryPriority.HasValue) parts.Add($"Mem {MemoryPriority.Value}");
            return string.Join(" · ", parts);
        }
    }

    public TunerThreadRule Clone() => new()
    {
        Description = Description,
        StartAddress = StartAddress,
        MatchAllThreads = MatchAllThreads,
        Priority = Priority,
        EfficiencyMode = EfficiencyMode,
        BoostEnabled = BoostEnabled,
        AffinityMask = AffinityMask,
        AffinityGroup = AffinityGroup,
        IdealGroup = IdealGroup,
        IdealIndex = IdealIndex,
        MemoryPriority = MemoryPriority,
        TargetCount = TargetCount,
    };
}

public sealed partial class TunerProfile : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Pattern { get; set; } = "*";
    [ObservableProperty] public partial uint? PriorityClass { get; set; }
    [ObservableProperty] public partial bool? BoostEnabled { get; set; }
    [ObservableProperty] public partial ulong? AffinityMask { get; set; }
    [ObservableProperty] public partial List<ulong>? CpuSetIds { get; set; }
    [ObservableProperty] public partial bool? EfficiencyMode { get; set; }
    [ObservableProperty] public partial bool AutoApply { get; set; } = true;
    
    [ObservableProperty] public partial bool GamingModeAuto { get; set; }
    [ObservableProperty] public partial bool ProtectThreads { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial ObservableCollection<TunerThreadRule> ThreadRules { get; set; } = new();
    [ObservableProperty] public partial string LastResult { get; set; } = "Waiting for process";

    [JsonIgnore]
    public AffinityScope PresetAffinity { get; set; } = AffinityScope.Unset;

    [JsonIgnore]
    public string ProcessActionsSummary
    {
        get
        {
        var parts = new List<string>();
        if (GamingModeAuto) parts.Add("Gaming mode");
        if (ProtectThreads) parts.Add("Protected");
        if (PriorityClass.HasValue) parts.Add("Priority");
        if (BoostEnabled.HasValue) parts.Add("Boost");
        if (EfficiencyMode.HasValue) parts.Add("Efficiency");
        if (AffinityMask.HasValue) parts.Add("Affinity");
        if (CpuSetIds != null && CpuSetIds.Count > 0) parts.Add("CPU Sets");
        return parts.Count > 0 ? string.Join(", ", parts) : "–";
        }
    }

    [JsonIgnore]
    public string ThreadRulesSummary
    {
        get
        {
            if (ThreadRules == null || ThreadRules.Count == 0) return "–";
            int targets = ThreadRules.Sum(r => r.TargetCount);
            string rules = ThreadRules.Count == 1 ? "1 rule" : $"{ThreadRules.Count} rules";
            string t = targets == 1 ? "1 target" : $"{targets} targets";
            return $"{rules} · {t}";
        }
    }

    [JsonIgnore]
    public string StatusText => Enabled ? "Enabled" : "Disabled";

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility EnabledPillVis =>
        Enabled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility DisabledPillVis =>
        Enabled ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility WaitingResultVis =>
        LastResult == "Waiting for process" || LastResult == "No actions defined"
            ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility AppliedResultVis =>
        LastResult == "Waiting for process" || LastResult == "No actions defined"
            ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    [JsonIgnore]
    public int ProcessActionCount =>
        (GamingModeAuto ? 1 : 0) +
        (ProtectThreads ? 1 : 0) +
        (PriorityClass.HasValue ? 1 : 0) +
        (BoostEnabled.HasValue ? 1 : 0) +
        (EfficiencyMode.HasValue ? 1 : 0) +
        (AffinityMask.HasValue ? 1 : 0) +
        ((CpuSetIds != null && CpuSetIds.Count > 0) ? 1 : 0);

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PriorityClass.HasValue) parts.Add($"Priority: {PriorityClass}");
            if (BoostEnabled.HasValue) parts.Add($"Boost: {BoostEnabled}");
            if (AffinityMask.HasValue) parts.Add($"Affinity: 0x{AffinityMask:X}");
            if (CpuSetIds != null && CpuSetIds.Count > 0) parts.Add($"Sets: {CpuSetIds.Count} assigned");
            if (EfficiencyMode.HasValue) parts.Add(EfficiencyMode.Value ? "Eco ON" : "Eco OFF");
            if (ProtectThreads) parts.Add("Protected: true");

            return parts.Count > 0 ? string.Join(" | ", parts) : "No changes defined";
        }
    }

    public void RefreshSummaries()
    {
        OnPropertyChanged(nameof(ProcessActionsSummary));
        OnPropertyChanged(nameof(ThreadRulesSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EnabledPillVis));
        OnPropertyChanged(nameof(DisabledPillVis));
        OnPropertyChanged(nameof(WaitingResultVis));
        OnPropertyChanged(nameof(AppliedResultVis));
        OnPropertyChanged(nameof(LastResult));
        OnPropertyChanged(nameof(ProcessActionCount));
        OnPropertyChanged(nameof(Summary));
    }

    public TunerProfile Clone()
    {
        var copy = new TunerProfile
        {
            Name = Name,
            Pattern = Pattern,
            PriorityClass = PriorityClass,
            BoostEnabled = BoostEnabled,
            AffinityMask = AffinityMask,
            CpuSetIds = CpuSetIds == null ? null : new List<ulong>(CpuSetIds),
            EfficiencyMode = EfficiencyMode,
            AutoApply = AutoApply,
            GamingModeAuto = GamingModeAuto,
            ProtectThreads = ProtectThreads,
            Enabled = Enabled,
            LastResult = LastResult,
            PresetAffinity = PresetAffinity,
        };
        if (ThreadRules != null)
        {
            foreach (var r in ThreadRules) copy.ThreadRules.Add(r.Clone());
        }
        return copy;
    }

    public void CopyFrom(TunerProfile source)
    {
        Name = source.Name;
        Pattern = source.Pattern;
        PriorityClass = source.PriorityClass;
        BoostEnabled = source.BoostEnabled;
        AffinityMask = source.AffinityMask;
        CpuSetIds = source.CpuSetIds == null ? null : new List<ulong>(source.CpuSetIds);
        EfficiencyMode = source.EfficiencyMode;
        AutoApply = source.AutoApply;
        GamingModeAuto = source.GamingModeAuto;
        ProtectThreads = source.ProtectThreads;
        Enabled = source.Enabled;
        LastResult = source.LastResult;
        PresetAffinity = source.PresetAffinity;
        ThreadRules.Clear();
        if (source.ThreadRules != null)
        {
            foreach (var r in source.ThreadRules) ThreadRules.Add(r.Clone());
        }
        RefreshSummaries();
    }
}
