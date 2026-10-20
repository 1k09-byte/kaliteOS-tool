using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.PackageManager.Models;

public enum PackageOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed partial class PackageOperationStatus : ObservableObject
{
    public required string Kind { get; init; }
    public required string PackageId { get; init; }
    public required string DisplayName { get; init; }

    [ObservableProperty]
    public partial PackageOperationState State { get; set; } = PackageOperationState.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(PercentValue))]
    public partial double? Percent { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsIndeterminate => Percent is null;
    public double PercentValue => Percent ?? 0;
}
