// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;

namespace kaliteConfig.Models;

/// <summary>Where the GPU does its scan-out scaling. Maps to the NvAPI scaling families.</summary>
public enum NvidiaScalingLocation
{
    /// <summary>Monitor performs the scaling (N*ToClosest / N*ScanOutToClosest* without GPUScanOut prefix).</summary>
    Display,
    /// <summary>GPU performs the scaling (the GPUScanOut* family).</summary>
    Gpu,
}

/// <summary>How non-native resolutions are stretched. Mirrors the NVIDIA Control Panel list.</summary>
public enum NvidiaScalingMode
{
    /// <summary>Preserve aspect ratio, letterbox/pillarbox as needed.</summary>
    AspectRatio = 0,
    /// <summary>Stretch to fill the panel edge-to-edge.</summary>
    Full = 1,
    /// <summary>No rescaling: render at the panel's native resolution.</summary>
    NoScaling = 2,
    /// <summary>Integer scaling: only whole-pixel downscales, no fractional blur.</summary>
    Integer = 3,
}

/// <summary>Color depth exposed by the NVIDIA driver (video output bpc).</summary>
public enum NvidiaColorDepthOption
{
    Bpc8 = 0,
    Bpc10 = 1,
}

/// <summary>
/// Output color space, i.e. the chroma sampling of the video signal. 4:4:4 keeps full
/// chroma resolution; 4:2:2 and 4:2:0 subsample it, which is only offered because the
/// driver reports it as supported.
/// </summary>
public enum NvidiaColorFormatOption
{
    Rgb = 0,
    YcbCr444 = 1,
    YcbCr422 = 2,
    YcbCr420 = 3,
}

/// <summary>User-facing names for the color format list, in enum order.</summary>
public static class NvidiaColorFormatLabels
{
    /// <summary>The dropdown contents, in the same order as <see cref="NvidiaColorFormatOption"/>.</summary>
    public static readonly string[] All = { "RGB", "YCbCr 4:4:4", "YCbCr 4:2:2", "YCbCr 4:2:0" };

    /// <summary>Display name for a single option, e.g. for the "was rejected" messages.</summary>
    public static string For(NvidiaColorFormatOption option) =>
        All.Length > (int)option ? All[(int)option] : option.ToString();
}

/// <summary>Digital range. VESA = full 0-255, CEA = limited 16-235.</summary>
public enum NvidiaDynamicRangeOption
{
    Full = 0,
    Limited = 1,
}

/// <summary>
/// A complete, read- or write-able snapshot of every display control this app exposes for
/// one NVIDIA-driven display, together with the per-feature capability flags the driver
/// reported. The driver, not this type, decides which fields are actually writable.
/// </summary>
public sealed class NvidiaDisplayProfile
{
    // ── identity ──
    // Everything here is a plain settable property rather than init-only: the WinUI XAML
    // type-info generator emits setters for types that reach a binding, and an init-only
    // member would make the generated XamlTypeInfo fail to compile.

    /// <summary>CCD device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Friendly monitor name from EDID, e.g. <c>LG ULTRAGEAR</c>. Empty when unknown.</summary>
    public string MonitorName { get; set; } = "";

    /// <summary>Connector the GPU drives, e.g. <c>DisplayPort</c>. Used only for labelling.</summary>
    public string ConnectionLabel { get; set; } = "";

    /// <summary>NvAPI display id, the key for every driver call.</summary>
    public uint DisplayId { get; set; }

    /// <summary>CCD target id, used to reach the OS-owned controls (HDR) for the same panel.</summary>
    public uint CcdTargetId { get; set; }

    /// <summary>
    /// Where this panel sits in the virtual desktop, and how big it is. The driver only
    /// knows the panel by id; DDC/CI can only be reached by pointing at a rectangle of
    /// screen, so the monitor-side controls need this geometry before they can be read
    /// or written at all.
    /// </summary>
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int PanelWidth { get; set; }
    public int PanelHeight { get; set; }

    /// <summary>False when the panel is driven by something other than an NVIDIA GPU.</summary>
    public bool IsNvidiaControlled { get; set; } = true;

    // ── capability flags (populated from the driver, never assumed) ──
    // These are settable because a profile is filled in progressively as each driver query
    // succeeds; identity stays init-only because it is fixed before any query runs.
    public bool SupportsDigitalVibrance { get; set; }
    public bool SupportsBrightness { get; set; }
    public bool SupportsContrast { get; set; }
    public bool SupportsColorGain { get; set; }
    public bool SupportsGamma { get; set; }
    public bool SupportsHue { get; set; }
    public bool SupportsScaling { get; set; }
    public bool SupportsColorData { get; set; }
    public bool SupportsHdr { get; set; }

    /// <summary>Raw DVC bounds reported by NvAPI_Disp_GetDVCInfo.</summary>
    public int DvcMinimum { get; set; }
    public int DvcMaximum { get; set; }
    public int DvcDefault { get; set; }

    // ── values ──

    /// <summary>Digital vibrance, normalized to the 0-100 scale the NVIDIA UI shows.</summary>
    public double DigitalVibrance { get; set; }

    /// <summary>Brightness, 0.0 - 1.0 (raw NVAPI float).</summary>
    public double Brightness { get; set; }

    /// <summary>Contrast, 0.0 - 1.0 (raw NVAPI float).</summary>
    public double Contrast { get; set; }

    /// <summary>
    /// True when the monitor implements the channel-gain control but would not report its
    /// current value. The sliders are still usable, but the held value is a placeholder
    /// rather than a reading, so <see cref="Diff"/> has to leave it alone until the user
    /// moves one. Brightness and contrast need no such flag: they are driver-side and the
    /// driver always has a value, defaulting to neutral.
    /// </summary>
    public bool ColorGainIsUnread { get; set; }

    /// <summary>
    /// True when the Windows session would not hand this display's channel gains a
    /// physical monitor at all. Distinct from a monitor that simply lacks the controls:
    /// here nothing ever answered, so the panel says so rather than showing a reading.
    /// </summary>
    public bool MonitorControlsUnreachable { get; set; }

    /// <summary>
    /// True when brightness and contrast were read from the graphics driver's own
    /// desktop-colour ramp, false when they came from the monitor over DDC/CI. The two
    /// are different hardware controls that happen to share a name, so a value read
    /// from one must never be written to the other — that is what this records.
    /// Gamma has no monitor-side equivalent and always rides the driver route.
    /// </summary>
    public bool ColourIsDriverSide { get; set; }

    /// <summary>
    /// Per-channel RGB gain, each 0.0 - 1.0. This is the NVIDIA "Color channel" control;
    /// it maps to DDC/CI VCP 0x16 / 0x18 / 0x1A.
    /// </summary>
    public (double Red, double Green, double Blue) ColorGain { get; set; }

    /// <summary>Gamma multiplier, 0.30 - 2.80 with 1.00 as neutral.</summary>
    public double Gamma { get; set; } = 1.0;

    /// <summary>Hue rotation in degrees, -180 to 180.</summary>
    public double Hue { get; set; }

    public NvidiaScalingLocation ScalingLocation { get; set; }
    public NvidiaScalingMode ScalingMode { get; set; }

    /// <summary>Windows Advanced Color (HDR). OS-owned; the driver only reports support.</summary>
    public bool HdrEnabled { get; set; }

    /// <summary>
    /// True when the panel supports HDR but Windows has force-disabled it, so it cannot be
    /// switched on from here however the toggle is set.
    /// </summary>
    public bool HdrBlocked { get; set; }

    public NvidiaColorDepthOption ColorDepth { get; set; }
    public NvidiaColorFormatOption ColorFormat { get; set; }
    public NvidiaDynamicRangeOption DynamicRange { get; set; }

    /// <summary>True when at least one control on this panel is writable.</summary>
    public bool HasAnyControl =>
        IsNvidiaControlled &&
        (SupportsDigitalVibrance || SupportsBrightness || SupportsContrast ||
         SupportsGamma || SupportsHue || SupportsScaling || SupportsColorData || SupportsHdr);

    /// <summary>
    /// Label for the display picker. A connector name is never presented as the display's
    /// name — it only appears as a qualifier, e.g. <c>LG ULTRAGEAR · \\.\DISPLAY1</c> or
    /// <c>\\.\DISPLAY1 (DisplayPort)</c> when EDID gave us nothing.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            bool named = !string.IsNullOrWhiteSpace(MonitorName);
            bool connected = !string.IsNullOrWhiteSpace(ConnectionLabel);
            if (named) return connected ? $"{MonitorName} · {DeviceName} · {ConnectionLabel}" : $"{MonitorName} · {DeviceName}";
            return connected ? $"{DeviceName} ({ConnectionLabel})" : DeviceName;
        }
    }

    public NvidiaDisplayProfile Clone() => (NvidiaDisplayProfile)MemberwiseClone();

    /// <summary>
    /// Returns a human-readable list of fields where <paramref name="other"/> differs from this
    /// snapshot. Used to decide what the Apply command actually has to push to the driver.
    /// </summary>
    public IReadOnlyList<string> Diff(NvidiaDisplayProfile other)
    {
        var changes = new List<string>();
        if (other is null) return changes;

        if (SupportsDigitalVibrance && Math.Abs(DigitalVibrance - other.DigitalVibrance) > 0.01)
            changes.Add(nameof(DigitalVibrance));
        // A control whose value is still a placeholder is skipped on the *other* side:
        // this snapshot's placeholder is only the starting point for a comparison, but a
        // placeholder coming in as a "desired" value is not something the user chose, and
        // writing it back would replace a setting this app never actually read. The flag
        // on the other side is cleared as soon as the user moves that control.
        if (SupportsBrightness && Math.Abs(Brightness - other.Brightness) > 0.001)
            changes.Add(nameof(Brightness));
        if (SupportsContrast && Math.Abs(Contrast - other.Contrast) > 0.001)
            changes.Add(nameof(Contrast));
        if (SupportsColorGain && !other.ColorGainIsUnread &&
            (Math.Abs(ColorGain.Red - other.ColorGain.Red) > 0.001 ||
             Math.Abs(ColorGain.Green - other.ColorGain.Green) > 0.001 ||
             Math.Abs(ColorGain.Blue - other.ColorGain.Blue) > 0.001))
            changes.Add(nameof(ColorGain));
        if (SupportsGamma && Math.Abs(Gamma - other.Gamma) > 0.001)
            changes.Add(nameof(Gamma));
        if (SupportsHue && Math.Abs(Hue - other.Hue) > 0.01)
            changes.Add(nameof(Hue));
        if (SupportsScaling && (ScalingLocation != other.ScalingLocation || ScalingMode != other.ScalingMode))
            changes.Add(nameof(ScalingLocation));
        if (SupportsHdr && HdrEnabled != other.HdrEnabled)
            changes.Add(nameof(HdrEnabled));
        if (SupportsColorData && ColorDepth != other.ColorDepth)
            changes.Add(nameof(ColorDepth));
        if (SupportsColorData && ColorFormat != other.ColorFormat)
            changes.Add(nameof(ColorFormat));
        if (SupportsColorData && DynamicRange != other.DynamicRange)
            changes.Add(nameof(DynamicRange));

        return changes;
    }
}
