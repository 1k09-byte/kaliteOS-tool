using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.PackageManager.Models;

public sealed partial class PackageInfo : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string SourceId { get; set; } = string.Empty;
    [ObservableProperty] public partial string SourceLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string Publisher { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    public partial string InstalledVersion { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    public partial string AvailableVersion { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty] public partial PackageOperationStatus? ActiveOperation { get; set; }

    public bool HasUpdate =>
        !string.IsNullOrWhiteSpace(AvailableVersion)
        && !string.Equals(AvailableVersion.Trim(), (InstalledVersion ?? "").Trim(),
            System.StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string VersionLine
    {
        get
        {
            bool hasInstalled = !string.IsNullOrWhiteSpace(InstalledVersion);
            bool hasAvailable = !string.IsNullOrWhiteSpace(AvailableVersion);
            if (hasInstalled && hasAvailable)
                return string.Equals(InstalledVersion.Trim(), AvailableVersion.Trim(),
                    System.StringComparison.OrdinalIgnoreCase)
                    ? InstalledVersion.Trim()
                    : $"{InstalledVersion.Trim()} → {AvailableVersion.Trim()}";
            if (hasAvailable) return AvailableVersion.Trim();
            if (hasInstalled) return InstalledVersion.Trim();
            return "-";
        }
    }
}
