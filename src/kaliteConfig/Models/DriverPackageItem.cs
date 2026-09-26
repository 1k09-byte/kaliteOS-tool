// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace kaliteConfig.Models;

/// <summary>
/// One third-party driver package from the Windows driver store
/// (an oem##.inf published name, as listed by pnputil /enum-drivers).
/// Inbox (Microsoft-shipped) drivers never appear - the store enumeration
/// used here only reports third-party packages.
/// </summary>
public sealed partial class DriverPackageItem : ObservableObject
{
    /// <summary>Published name, e.g. oem42.inf - the delete-driver key.</summary>
    [ObservableProperty] public partial string PublishedName { get; set; } = string.Empty;
    [ObservableProperty] public partial string OriginalName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Provider { get; set; } = string.Empty;
    [ObservableProperty] public partial string ClassName { get; set; } = string.Empty;
    [ObservableProperty] public partial string DriverVersion { get; set; } = string.Empty;
    [ObservableProperty] public partial string DriverDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string SignerName { get; set; } = string.Empty;

    /// <summary>Approx on-disk size of the FileRepository payload, -1 when unknown.</summary>
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

    /// <summary>
    /// Raw driver class ("AudioProcessingObject", "HIDClass", …) mapped to
    /// plain language. Unknown classes fall back to the raw value untouched.
    /// </summary>
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

    /// <summary>
    /// Human-readable row title, e.g. "NVIDIA display driver".
    /// Falls back to the INF name when there is nothing friendlier to say.
    /// </summary>
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

    /// <summary>Secondary row line: provider plus the technical names.</summary>
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
