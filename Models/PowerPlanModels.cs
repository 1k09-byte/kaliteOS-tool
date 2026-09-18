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

    public bool IsChoiceType => Type == 2u && PossibleChoices.Count > 0;
    public bool IsBooleanType => !IsChoiceType && PossibleChoices.Count == 2
        && PossibleChoices.Any(c => c.ValueIndex == 0) && PossibleChoices.Any(c => c.ValueIndex == 1);
    public bool IsRangeType => !IsChoiceType && !IsBooleanType;

    partial void OnTypeChanged(uint value)
    {
        OnPropertyChanged(nameof(IsChoiceType));
        OnPropertyChanged(nameof(IsBooleanType));
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

    /// <summary>Friendly name of the current AC value ("Balanced", "On", …) for range types' caption.</summary>
    public string AcValueText
    {
        get
        {
            var c = PossibleChoices.FirstOrDefault(x => (uint)AcValueIndex == x.ValueIndex);
            return c?.Name ?? $"{AcValueIndex:0}";
        }
    }

    /// <summary>Friendly name of the current DC value.</summary>
    public string DcValueText
    {
        get
        {
            var c = PossibleChoices.FirstOrDefault(x => (uint)DcValueIndex == x.ValueIndex);
            return c?.Name ?? $"{DcValueIndex:0}";
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
