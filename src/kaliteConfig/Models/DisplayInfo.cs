// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.// ==============================================================================
using System.Collections.Generic;

namespace kaliteConfig.Models;

/// <summary>
/// Immutable identity and live state snapshot of a single connected display.
/// Populated by <see cref="Services.DisplayEnumerationService"/>.
/// </summary>
public sealed class DisplayInfo
{
    // ── CCD identifiers (for API calls) ──
    public required uint CcdSourceId { get; init; }
    public required uint CcdTargetId { get; init; }
    internal Native.NativeMethods.Display.LUID AdapterId { get; init; }

    /// <summary>
    /// Adapter of the *source* for this path, which is not necessarily
    /// <see cref="AdapterId"/> (that one belongs to the target). Per-monitor DPI scale is
    /// a property of the source, so changing it needs this pair rather than the target's.
    /// </summary>
    internal Native.NativeMethods.Display.LUID SourceAdapterId { get; init; }

    // ── GDI device name (e.g. \\.\DISPLAY1) ──
    public required string DeviceName { get; init; }

    // ── Friendly name from CCD (e.g. "LG ULTRAGEAR") ──
    public string FriendlyName { get; init; } = "";

    // ── EDID-parsed identity ──
    public string Manufacturer { get; init; } = "Unknown";
    public string ProductCode { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public int YearOfManufacture { get; init; }

    /// <summary>Native (preferred) resolution parsed from EDID detailed timing.</summary>
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }

    /// <summary>Number of EDID extension blocks (from byte 126 of the base block).</summary>
    public int ExtensionBlockCount { get; init; }

    // ── Connection type ──
    internal Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY ConnectionType { get; init; }

    /// <summary>Human-readable connection name (HDMI, DisplayPort, DVI, etc.).</summary>
    public string ConnectionTypeDisplay => ConnectionType switch
    {
        Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hdmi => "HDMI",
        Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayportExternal => "DisplayPort",
        Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayportEmbedded => "eDP",
        Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Dvi => "DVI",
        Native.NativeMethods.Display.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hd15 => "VGA",
        _ => ConnectionType.ToString(),
    };

    // ── Registry key path (for identification) ──
    public string RegistryKeyPath { get; init; } = "";

    // ── Live state ──
    public bool IsActive { get; init; }
    public bool IsPrimary { get; init; }

    // ── Current active resolution (from CCD source mode) ──
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public double CurrentRefreshRate { get; init; }
    public int CurrentRotationDegrees { get; init; }
    public int CurrentScalePercent { get; init; } = 100;

    /// <summary>Scale percentages this display accepts. See the scale service.</summary>
    public IReadOnlyList<int> AvailableScalePercents { get; init; } =
        new[] { 100, 125, 150, 175, 200, 225, 250, 300 };

    /// <summary>
    /// False when the OS refused the DPI-scale packet for this display, which is the only
    /// honest reason to disable the scale picker.
    /// </summary>
    public bool IsScaleSupported { get; init; } = true;

    /// <summary>Short label for the arrangement canvas, e.g. "LG ULTRAGEAR · 2560×1440 @ 165 Hz".</summary>
    public string ArrangementLabel =>
        string.IsNullOrEmpty(FriendlyName)
            ? $"{DeviceName} · {CurrentWidth}×{CurrentHeight}"
            : $"{FriendlyName} · {CurrentWidth}×{CurrentHeight} @ {CurrentRefreshRate:F0} Hz";

    /// <summary>Position in the virtual desktop (from CCD source mode).</summary>
    public int PositionX { get; init; }
    public int PositionY { get; init; }
}
