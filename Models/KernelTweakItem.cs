using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models;

public sealed partial class KernelTweakItem : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _stateText = string.Empty;
}
