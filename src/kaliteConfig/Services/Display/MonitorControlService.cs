// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using kaliteConfig.Native;

namespace kaliteConfig.Services;

/// <summary>Read/write access to the monitor controls the OS exposes over DDC/CI.</summary>
internal static class MonitorControlService
{
    /// <summary>Per-channel RGB gain, i.e. the NVIDIA "Color channel" control.</summary>
    public readonly record struct RgbGain(double Red, double Green, double Blue)
    {
        public static RgbGain None => new(0, 0, 0);

        public bool IsKnown => Red > 0.001 && Green > 0.001 && Blue > 0.001;
    }

    /// <summary>
    /// One DDC/CI feature as the driver reports it.
    ///
    /// <see cref="Supported"/> and <see cref="ValueKnown"/> are deliberately separate.
    /// A monitor routinely answers a VCP query with a valid maximum but refuses the
    /// current value — that is what happens when the panel is asleep, in a low-power
    /// state, or driven over a link the driver will not interrogate. The feature is
    /// implemented, so the control must stay usable; only the reading of it is missing.
    /// Collapsing the two states into one flag is what used to make brightness and
    /// contrast look permanently dead on those panels.
    /// </summary>
    internal readonly record struct Feature(bool Supported, bool ValueKnown, double Fraction, uint Maximum)
    {
        /// <summary>A feature the monitor does not implement at all.</summary>
        public static Feature Unsupported => new(false, false, 0, 0);

        /// <summary>Implemented, but the current value could not be read this time.</summary>
        public static Feature Unreadable(uint maximum) => new(true, false, 0.5, maximum);

        public static Feature Known(uint current, uint maximum) =>
            new(true, true, maximum == 0 ? 0 : current / (double)maximum, maximum);
    }

    /// <summary>
    /// The DDC/CI standard defines a maximum of 100 for the non-colour VCP codes. It is
    /// used only when the monitor reports no maximum of its own, which is the last case
    /// where a write can still be attempted with a sane raw value.
    /// </summary>
    private const uint StandardMaximum = 100;

    /// <summary>
    /// Reads brightness, contrast and per-channel gain as 0.0-1.0 fractions. Each result
    /// carries its own support flag, because monitors vary wildly in which VCP codes they
    /// implement and a monitor that reports brightness but not contrast is common.
    /// </summary>
    public static void Read(int originX, int originY, int width, int height,
        out Feature brightness, out Feature contrast, out RgbGain gain, out Feature gainFeature,
        out bool monitorReachable, Action<string>? log = null)
    {
        brightness = Feature.Unsupported;
        contrast = Feature.Unsupported;
        gain = RgbGain.None;
        gainFeature = Feature.Unsupported;
        monitorReachable = false;

        log?.Invoke($"open monitor at ({originX},{originY})");
        if (!NativeMethods.DdcCi.TryOpenPhysicalMonitor(originX, originY, width, height, out IntPtr monitor))
        {
            // The display stack would not hand over a physical monitor. That is a
            // different thing from the monitor lacking the controls: the panel reports
            // these as "cannot reach the monitor" rather than pretending the monitor
            // answered with no.
            log?.Invoke("no physical monitor handle: this Windows session cannot map the " +
                        "display to a physical monitor, so DDC/CI is unavailable here");
            return;
        }

        monitorReachable = true;

        try
        {
            brightness = ReadFeature(monitor, NativeMethods.DdcCi.VCP_BRIGHTNESS, "brightness", log);
            contrast = ReadFeature(monitor, NativeMethods.DdcCi.VCP_CONTRAST, "contrast", log);

            Feature r = ReadFeature(monitor, NativeMethods.DdcCi.VCP_RED_GAIN, "red gain", log);
            Feature g = ReadFeature(monitor, NativeMethods.DdcCi.VCP_GREEN_GAIN, "green gain", log);
            Feature b = ReadFeature(monitor, NativeMethods.DdcCi.VCP_BLUE_GAIN, "blue gain", log);

            // The three channels are one control to the user, so they are only offered
            // together, and only when all three can actually be placed on a scale.
            bool allReadable = r.Supported && g.Supported && b.Supported;
            if (allReadable)
            {
                gain = new RgbGain(
                    r.ValueKnown ? r.Fraction : 1.0,
                    g.ValueKnown ? g.Fraction : 1.0,
                    b.ValueKnown ? b.Fraction : 1.0);
                gainFeature = new Feature(
                    Supported: true,
                    ValueKnown: r.ValueKnown && g.ValueKnown && b.ValueKnown,
                    Fraction: 1.0,
                    Maximum: Math.Max(r.Maximum, Math.Max(g.Maximum, b.Maximum)));
            }
            else
            {
                gainFeature = Feature.Unsupported;
            }

            log?.Invoke(r.Supported || g.Supported || b.Supported
                ? $"gain R{gain.Red:F2} G{gain.Green:F2} B{gain.Blue:F2} " +
                  $"(readable r={r.ValueKnown} g={g.ValueKnown} b={b.ValueKnown})"
                : $"no RGB gain (0x16/0x18/0x1A): r={r.Supported} g={g.Supported} b={b.Supported}");
        }
        finally
        {
            NativeMethods.DdcCi.ClosePhysicalMonitor(monitor);
        }
    }

    /// <summary>
    /// Reads a single VCP feature. A reply that carries a maximum but no usable current
    /// value still counts as implemented, which is what keeps the control enabled.
    /// </summary>
    private static Feature ReadFeature(IntPtr monitor, byte vcpCode, string name, Action<string>? log)
    {
        bool ok = NativeMethods.DdcCi.TryGetVcp(monitor, vcpCode, out uint current, out uint maximum);
        if (maximum == 0)
        {
            log?.Invoke($"no {name} (0x{vcpCode:X2})");
            return Feature.Unsupported;
        }

        if (!ok || current > maximum)
        {
            log?.Invoke($"{name} 0x{vcpCode:X2} implemented but the current value is unreadable (max {maximum})");
            return Feature.Unreadable(maximum);
        }

        log?.Invoke($"{name} {current}/{maximum}");
        return Feature.Known(current, maximum);
    }

    /// <summary>Writes whichever of the three values is supplied. Others are left untouched.</summary>
    public static bool Write(int originX, int originY,
        double? brightness, double? contrast, RgbGain? gain)
        => Write(originX, originY, 0, 0, brightness, contrast, gain);

    /// <summary>
    /// <see cref="Write(int,int,double?,double?,RgbGain?)"/> with the display's size, which
    /// lets the monitor also be addressed by its centre rather than only by its corner.
    /// </summary>
    public static bool Write(int originX, int originY, int width, int height,
        double? brightness, double? contrast, RgbGain? gain)
    {
        if (brightness is null && contrast is null && gain is null) return true;

        if (!NativeMethods.DdcCi.TryOpenPhysicalMonitor(originX, originY, width, height, out IntPtr monitor))
            return false;

        try
        {
            bool ok = true;
            if (brightness.HasValue) ok &= TrySetFraction(monitor, NativeMethods.DdcCi.VCP_BRIGHTNESS, brightness.Value);
            if (contrast.HasValue) ok &= TrySetFraction(monitor, NativeMethods.DdcCi.VCP_CONTRAST, contrast.Value);
            if (gain.HasValue)
            {
                ok &= TrySetFraction(monitor, NativeMethods.DdcCi.VCP_RED_GAIN, gain.Value.Red);
                ok &= TrySetFraction(monitor, NativeMethods.DdcCi.VCP_GREEN_GAIN, gain.Value.Green);
                ok &= TrySetFraction(monitor, NativeMethods.DdcCi.VCP_BLUE_GAIN, gain.Value.Blue);
            }
            return ok;
        }
        finally
        {
            NativeMethods.DdcCi.ClosePhysicalMonitor(monitor);
        }
    }

    /// <summary>
    /// Converts a 0.0-1.0 fraction into the monitor's own scale before sending it.
    ///
    /// The scale is taken from the query even when the query reports a failure, because a
    /// monitor that will not hand over its current value often still reports its maximum
    /// and still accepts writes. Only when no maximum is available at all does this fall
    /// back to the DDC/CI standard scale — refusing the write there would leave the
    /// control permanently dead on exactly the panels that need it.
    /// </summary>
    private static bool TrySetFraction(IntPtr monitor, byte vcpCode, double fraction)
    {
        NativeMethods.DdcCi.TryGetVcp(monitor, vcpCode, out _, out uint reported);
        uint maximum = reported > 0 ? reported : StandardMaximum;
        return NativeMethods.DdcCi.TrySetVcp(monitor, vcpCode, ToRawValue(fraction, maximum));
    }

    /// <summary>
    /// The raw VCP value for a 0.0-1.0 fraction on a monitor whose scale runs to
    /// <paramref name="maximum"/>. Separated from the P/Invoke so the rounding and clamping
    /// can be checked without a physical monitor.
    /// </summary>
    internal static uint ToRawValue(double fraction, uint maximum)
    {
        if (maximum == 0) maximum = StandardMaximum;
        long value = (long)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * maximum);
        return (uint)Math.Clamp(value, 0L, maximum);
    }
}
