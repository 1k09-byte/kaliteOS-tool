using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.PackageManager.Models;

/// <summary>Outcome + progress of one queued package operation.</summary>
public enum PackageOperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Bindable per-operation record (row progress + history).</summary>
public sealed partial class PackageOperationStatus : ObservableObject
{
    public required string Kind { get; init; }
    public required string PackageId { get; init; }
    public required string DisplayName { get; init; }

    [ObservableProperty]
    public partial PackageOperationState State { get; set; } = PackageOperationState.Queued;

    /// <summary>0-100 when the backend reports it, else null (indeterminate).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(PercentValue))]
    public partial double? Percent { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime UpdatedAt { get; set; } = DateTime.Now;

    // Helpers for XAML binding since WinUI x:Bind can stumble on nullables
    public bool IsIndeterminate => Percent is null;
    public double PercentValue => Percent ?? 0;
}
