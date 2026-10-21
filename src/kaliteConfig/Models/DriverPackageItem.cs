using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace kaliteConfig.Models;

public sealed partial class DriverPackageItem : ObservableObject
{
    [ObservableProperty] public partial string PublishedName { get; set; } = string.Empty;
    [ObservableProperty] public partial string OriginalName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Provider { get; set; } = string.Empty;
    [ObservableProperty] public partial string ClassName { get; set; } = string.Empty;
    [ObservableProperty] public partial string DriverVersion { get; set; } = string.Empty;
    [ObservableProperty] public partial string DriverDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string SignerName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    public partial long StoreSizeBytes { get; set; } = -1;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public bool HasSize => StoreSizeBytes > 0;

    public string Initial
    {
        get
        {
            var s = string.IsNullOrWhiteSpace(Provider) ? OriginalName : Provider;
            return string.IsNullOrEmpty(s) ? "?" : s.Substring(0, 1).ToUpperInvariant();
        }
    }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(OriginalName) ? PublishedName : OriginalName;

    public string FriendlyClass => ClassName.Trim().ToLowerInvariant() switch
    {
        "display" or "display adapters" => "display",
        "net" => "network",
        "netservice" => "network service",
        "media" => "media",
        "audioprocessingobject" => "audio",
        "hidclass" => "input",
        "usb" => "USB",
        "system" => "system",
        "printer" => "printer",
        "extension" => "extension",
        "softwarecomponent" or "softwaredevice" => "software",
        _ => ClassName.Trim(),
    };

    public string FriendlyKind
    {
        get
        {
            string kind = FriendlyClass;
            if (string.IsNullOrWhiteSpace(kind)) return DisplayName;
            if (string.IsNullOrWhiteSpace(Provider)) return Capitalize(kind) + " driver";
            return $"{Provider.Trim()} {kind} driver";
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Provider)) parts.Add(Provider.Trim());
            string inf = DisplayName.Trim();
            if (inf.Length > 0) parts.Add(inf);
            string oem = PublishedName.Trim();
            if (oem.Length > 0 && !string.Equals(oem, inf, StringComparison.OrdinalIgnoreCase))
                parts.Add(oem);
            return parts.Count == 0 ? "?" : string.Join(" · ", parts);
        }
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    public string FormattedSize
    {
        get
        {
            long size = StoreSizeBytes;
            if (size <= 0) return string.Empty;
            if (size < 1024) return $"{size} B";
            if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
            if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024.0):F1} MB";
            return $"{size / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
    }
}
