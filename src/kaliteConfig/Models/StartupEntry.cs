using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models;

public enum StartupEntryKind
{
    RunUser,
    RunMachine,
    Service,
    ScheduledTask,
}

public sealed partial class StartupEntry : ObservableObject
{
    [ObservableProperty] public partial StartupEntryKind Kind { get; set; }

    [ObservableProperty] public partial string Id { get; set; } = string.Empty;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial string EntryType { get; set; } = string.Empty;

    [ObservableProperty] public partial string Command { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    public string Company { get; set; } = string.Empty;
}
