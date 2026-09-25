using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models;

public sealed partial class KernelTweakItem : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsOn { get; set; }
    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;
}
