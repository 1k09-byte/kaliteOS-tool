using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace stellarisKIT.Models;

public sealed partial class PowerScheme : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBuiltIn;
    
    public ObservableCollection<PowerSubgroup> Subgroups { get; } = new();
}

public sealed partial class PowerSubgroup : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    
    public ObservableCollection<PowerSetting> Settings { get; } = new();
}

public sealed partial class PowerSetting : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    
    // 0 = Range, 1 = Boolean, 2 = Choices (Enum)
    [ObservableProperty] private uint _type;
    
    [ObservableProperty] private double _acValueIndex;
    [ObservableProperty] private double _dcValueIndex;
    
    // For Range/Enum types
    public ObservableCollection<PowerSettingChoice> PossibleChoices { get; } = new();
}

public sealed class PowerSettingChoice
{
    public uint ValueIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public override string ToString() => Name;
}
