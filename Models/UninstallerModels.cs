using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace kaliteConfig.Models;

public enum InstallType
{
    Registry,
    Msi,
    AppX
}

public sealed partial class UninstallerItem : ObservableObject
{
    // ProductCode, PFN, or Registry Key Name
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Publisher { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string Version { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string InstallDate { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string InstallLocation { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string UninstallString { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string QuietUninstallString { get; set; } = string.Empty;
    [ObservableProperty]
    public partial InstallType InstallType { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSize))]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    [NotifyPropertyChangedFor(nameof(ExactSizeBytes))]
    [NotifyPropertyChangedFor(nameof(SizeBarValue))]
    // In bytes
    public partial long EstimatedSize { get; set; }
    [ObservableProperty]
    public partial bool IsSystemComponent { get; set; }
    [ObservableProperty]
    public partial string DisplayIcon { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRealIcon))]
    public partial BitmapImage? Icon { get; set; }
    public bool HasRealIcon => Icon != null;
    public bool IsStoreApp => InstallType == InstallType.AppX;
    public string SourceLabel => InstallType switch { InstallType.AppX => "STORE", InstallType.Msi => "MSI", _ => "APP" };
    public string FormattedInstallDate
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallDate)) return string.Empty;
            // Registry dates are usually yyyyMMdd; Store dates vary - parse leniently.
            if (InstallDate.Length == 8 &&
                int.TryParse(InstallDate.Substring(0, 4), out int y) &&
                int.TryParse(InstallDate.Substring(4, 2), out int m) &&
                int.TryParse(InstallDate.Substring(6, 2), out int d))
            {
                try { return new DateTime(y, m, d).ToShortDateString(); } catch { }
            }
            if (DateTime.TryParse(InstallDate, out var dt)) return dt.ToShortDateString();
            return InstallDate;
        }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSize))]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    [NotifyPropertyChangedFor(nameof(ExactSizeBytes))]
    [NotifyPropertyChangedFor(nameof(SizeBarValue))]
    // real on-disk size, bytes
    public partial long ComputedSize { get; set; }
    public bool HasSize => EffectiveSize > 0;

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);
    public long EffectiveSize => ComputedSize > 0 ? ComputedSize : EstimatedSize;
    public string ExactSizeBytes => EffectiveSize <= 0 ? "Size unknown" : $"{EffectiveSize:N0} bytes";
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
    public string Glyph => InstallType switch { InstallType.AppX => "\uE8F9", InstallType.Msi => "\uE74C", _ => "\uE718" };
    // Avatar color derived from name hash (stable per app)
    public string AvatarBrushKey => $"Avatar{(Math.Abs(Name.GetHashCode()) % 6)}";
    public double SizeBarValue
    {
        get
        {
            if (EffectiveSize <= 0) return 0;
            // log scale 0..1 across B..100GB
            double v = Math.Log10((double)EffectiveSize + 1) / Math.Log10(100d * 1024 * 1024 * 1024);
            return Math.Clamp(v, 0.03, 1.0);
        }
    }
    
    // For UI batch selection
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
    
    public string FormattedSize
    {
        get
        {
            long size = EffectiveSize;
            if (size == 0) return string.Empty;
            if (size < 1024) return $"{size} B";
            if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
            if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024.0):F1} MB";
            return $"{size / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }
}
