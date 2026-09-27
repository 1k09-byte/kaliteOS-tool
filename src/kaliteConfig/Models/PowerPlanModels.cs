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
}

public sealed partial class PowerSubgroup : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    
    public ObservableCollection<PowerSetting> Settings { get; } = new();
}

public sealed partial class PowerSetting : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    
    // 0 = Range, 1 = Boolean, 2 = Choices (Enum)
    [ObservableProperty]
    public partial uint Type { get; set; }
    
    [ObservableProperty]
    public partial double AcValueIndex { get; set; }
    [ObservableProperty]
    public partial double DcValueIndex { get; set; }
    
    // For Range/Enum types
    public ObservableCollection<PowerSettingChoice> PossibleChoices { get; } = new();

    public bool IsChoiceType => Type == 2u && PossibleChoices.Count > 0 && !IsPresetList;
    public bool IsBooleanType => !IsChoiceType && !IsPresetList && PossibleChoices.Count == 2
        && PossibleChoices.Any(c => c.ValueIndex == 0) && PossibleChoices.Any(c => c.ValueIndex == 1);
    public bool IsRangeType => !IsChoiceType && !IsBooleanType;
    /// <summary>
    /// Range whose named options come from the OS itself rather than from
    /// <see cref="IsPresetList"/>.
    ///
    /// The !IsPresetList guard is load-bearing. A preset list IS a range with
    /// named options, so without this clause both flags were true at once and
    /// the page rendered FOUR editors per setting (an AC and a DC combo from
    /// this branch, plus the richer AC and DC combo+number pairs from the
    /// preset branch) instead of the intended two. The preset branch already
    /// shows the worded picker, so claiming it here duplicated the control.
    /// </summary>
    public bool HasRangeChoices => IsRangeType && PossibleChoices.Count > 0 && !IsPresetList;
    /// <summary>
    /// Range with no named options at all: numeric entry plus a friendly
    /// caption. Excludes preset lists for the same reason as
    /// <see cref="HasRangeChoices"/> - a setting the catalog gave words to is
    /// never a "bare" range, even in the degenerate case where the word list
    /// came out empty.
    /// </summary>
    public bool IsBareRange => IsRangeType && PossibleChoices.Count == 0 && !IsPresetList;

    /// <summary>
    /// Set by <see cref="PowerService"/> when the named values are PRESETS for
    /// a continuous setting ("Never", "1 minute") rather than a real
    /// enumeration. The worded picker and the raw number box are then both
    /// shown: words are the obvious way in, and the number box keeps an
    /// arbitrary value reachable instead of stranding it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPresetList { get; set; }

    /// <summary>True when the worded picker should be offered at all.</summary>
    public bool HasWordedPicker => PossibleChoices.Count > 0;

    /// <summary>
    /// Every derived editor flag depends on this one, so flipping it has to
    /// republish all of them. Relying on Type being assigned afterwards made
    /// the result depend on statement order inside <see cref="PowerService"/>,
    /// which is exactly how the four-editor bug survived.
    /// </summary>
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
            // Native Windows Control Panel exposes most timeouts (disk after, sleep after, hibernate after) in minutes.
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

    /// <summary>Selected friendly option for the AC (plugged in) value.</summary>
    public PowerSettingChoice? SelectedAcChoice
    {
        get => PossibleChoices.FirstOrDefault(c => (uint)AcValueIndex == c.ValueIndex);
        set
        {
            if (value is null) return;
            if ((uint)AcValueIndex == value.ValueIndex) return;
            AcValueIndex = value.ValueIndex; // fires AcValueIndex → ViewModel writes AC
            OnPropertyChanged(nameof(SelectedAcChoice));
            OnPropertyChanged(nameof(AcValueText));
        }
    }

    /// <summary>Selected friendly option for the DC (battery) value.</summary>
    public PowerSettingChoice? SelectedDcChoice
    {
        get => PossibleChoices.FirstOrDefault(c => (uint)DcValueIndex == c.ValueIndex);
        set
        {
            if (value is null) return;
            if ((uint)DcValueIndex == value.ValueIndex) return;
            DcValueIndex = value.ValueIndex; // fires DcValueIndex → ViewModel writes DC
            OnPropertyChanged(nameof(SelectedDcChoice));
            OnPropertyChanged(nameof(DcValueText));
        }
    }

    /// <summary>Boolean toggle for the AC value (On/Off naming from possible values).</summary>
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

    /// <summary>Boolean toggle for the DC value.</summary>
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

    /// <summary>Friendly value for range captions: named choice, else a humanized
    /// unit ("20 minutes", "200 ms", "100 %") instead of a bare number.</summary>
    public string AcValueText
    {
        get
        {
            var c = PossibleChoices.FirstOrDefault(x => (uint)AcValueIndex == x.ValueIndex);
            if (c is not null) return c.Name;
            return HumanizeValue(AcValueIndex);
        }
    }

    /// <summary>Unit inferred from the setting name. Power scheme values carry no unit
    /// metadata, so well-established Windows conventions are matched by keyword.</summary>
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

    /// <summary>Friendly name of the current DC value.</summary>
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
