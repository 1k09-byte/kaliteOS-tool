using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace kaliteConfig.Models;

public sealed partial class PowerScheme : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsActive { get; set; }
    [ObservableProperty]
    public partial bool IsBuiltIn { get; set; }
    
    public ObservableCollection<PowerSubgroup> Subgroups { get; } = new();

    public ObservableCollection<PowerSubgroup> FilteredSubgroups { get; } = new();
}

public sealed partial class PowerSubgroup : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = false;

    public ObservableCollection<PowerSetting> Settings { get; } = new();

    public ObservableCollection<PowerSetting> FilteredSettings { get; } = new();
}

public sealed partial class PowerSetting : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial uint Type { get; set; }
    
    [ObservableProperty]
    public partial double AcValueIndex { get; set; }
    [ObservableProperty]
    public partial double DcValueIndex { get; set; }

    public ObservableCollection<PowerSettingChoice> PossibleChoices { get; } = new();

    public bool IsChoiceType => Type == 2u && PossibleChoices.Count > 0 && !IsPresetList;
    public bool IsBooleanType => !IsChoiceType && !IsPresetList && PossibleChoices.Count == 2
        && PossibleChoices.Any(c => c.ValueIndex == 0) && PossibleChoices.Any(c => c.ValueIndex == 1);
    public bool IsRangeType => !IsChoiceType && !IsBooleanType;
    public bool HasRangeChoices => IsRangeType && PossibleChoices.Count > 0 && !IsPresetList;
    public bool IsBareRange => IsRangeType && PossibleChoices.Count == 0 && !IsPresetList;

    [ObservableProperty]
    public partial bool IsPresetList { get; set; }

    public bool HasWordedPicker => PossibleChoices.Count > 0;

    partial void OnIsPresetListChanged(bool value) => RaiseEditorFlags();

    partial void OnTypeChanged(uint value) => RaiseEditorFlags();

    private void RaiseEditorFlags()
    {
        OnPropertyChanged(nameof(IsChoiceType));
        OnPropertyChanged(nameof(IsBooleanType));
        OnPropertyChanged(nameof(IsRangeType));
        OnPropertyChanged(nameof(HasRangeChoices));
        OnPropertyChanged(nameof(IsBareRange));
        OnPropertyChanged(nameof(IsPresetList));
    }

    partial void OnAcValueIndexChanged(double value)
    {
        OnPropertyChanged(nameof(AcValueText));
        OnPropertyChanged(nameof(UiAcValue));
    }

    partial void OnDcValueIndexChanged(double value)
    {
        OnPropertyChanged(nameof(DcValueText));
        OnPropertyChanged(nameof(UiDcValue));
    }

    private double UIMultiplier
    {
        get
        {
            var n = Name ?? string.Empty;
            if (n.Contains("after", StringComparison.OrdinalIgnoreCase) && !n.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) return 60.0;
            return 1.0;
        }
    }

    public double UiAcValue
    {
        get => AcValueIndex / UIMultiplier;
        set
        {
            var raw = Math.Round(value * UIMultiplier);
            if (AcValueIndex == raw) return;
            AcValueIndex = raw;
        }
    }

    public double UiDcValue
    {
        get => DcValueIndex / UIMultiplier;
        set
        {
            var raw = Math.Round(value * UIMultiplier);
            if (DcValueIndex == raw) return;
            DcValueIndex = raw;
        }
    }

    public PowerSettingChoice? SelectedAcChoice
    {
        get => PossibleChoices.FirstOrDefault(c => (uint)AcValueIndex == c.ValueIndex);
        set
        {
            if (value is null) return;
            if ((uint)AcValueIndex == value.ValueIndex) return;
            AcValueIndex = value.ValueIndex;
            OnPropertyChanged(nameof(SelectedAcChoice));
            OnPropertyChanged(nameof(AcValueText));
        }
    }

    public PowerSettingChoice? SelectedDcChoice
    {
        get => PossibleChoices.FirstOrDefault(c => (uint)DcValueIndex == c.ValueIndex);
        set
        {
            if (value is null) return;
            if ((uint)DcValueIndex == value.ValueIndex) return;
            DcValueIndex = value.ValueIndex;
            OnPropertyChanged(nameof(SelectedDcChoice));
            OnPropertyChanged(nameof(DcValueText));
        }
    }

    public bool AcBoolValue
    {
        get => (uint)AcValueIndex == 1;
        set
        {
            if ((uint)AcValueIndex == (value ? 1u : 0u)) return;
            AcValueIndex = value ? 1 : 0;
            OnPropertyChanged(nameof(AcBoolValue));
        }
    }

    public bool DcBoolValue
    {
        get => (uint)DcValueIndex == 1;
        set
        {
            if ((uint)DcValueIndex == (value ? 1u : 0u)) return;
            DcValueIndex = value ? 1 : 0;
            OnPropertyChanged(nameof(DcBoolValue));
        }
    }

    public string AcValueText
    {
        get
        {
            var c = PossibleChoices.FirstOrDefault(x => (uint)AcValueIndex == x.ValueIndex);
            if (c is not null) return c.Name;
            return HumanizeValue(AcValueIndex);
        }
    }

    private string ValueUnit
    {
        get
        {
            var n = Name ?? string.Empty;
            if (n.Contains("HIPM/DIPM", StringComparison.OrdinalIgnoreCase)) return "hipm";
            if (n.Contains("Power Level", StringComparison.OrdinalIgnoreCase)) return "percent";
            if (n.Contains("NVMe", StringComparison.OrdinalIgnoreCase) &&
                (n.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Latency", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("Tolerance", StringComparison.OrdinalIgnoreCase))) return "ms";
            if (n.Contains("AHCI", StringComparison.OrdinalIgnoreCase) &&
                n.Contains("Adaptive", StringComparison.OrdinalIgnoreCase)) return "ms";
            if (n.Contains("Second", StringComparison.OrdinalIgnoreCase)) return "s";
            if (n.Contains("Disk", StringComparison.OrdinalIgnoreCase) &&
                (n.Contains("after", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("burst", StringComparison.OrdinalIgnoreCase))) return "s";
            return "";
        }
    }

    private string HumanizeValue(double v)
    {
        switch (ValueUnit)
        {
            case "hipm":
                return (uint)v switch { 0 => "Active", 1 => "HIPM", 2 => "DIPM", _ => $"{v:0}" };
            case "percent":
                return $"{v:0} %";
            case "ms":
                if (v >= 1000 && v % 1000 == 0) return $"{v / 1000:0} s";
                return $"{v:0} ms";
            case "s":
                if (v == 0) return "Never";
                if (v >= 90) return $"{v / 60:0.##} minutes";
                return $"{v:0} seconds";
            default:
                return $"{v:0}";
        }
    }

    public string DcValueText
    {
        get
        {
            var c = PossibleChoices.FirstOrDefault(x => (uint)DcValueIndex == x.ValueIndex);
            if (c is not null) return c.Name;
            return HumanizeValue(DcValueIndex);
        }
    }
}

public sealed class PowerSettingChoice
{
    public uint ValueIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public override string ToString() => Name;
}
