// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using kaliteConfig.Native;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>
/// The NVIDIA driver's own desktop colour controls — the ones behind
/// "Adjust desktop color settings" in the NVIDIA Control Panel.
///
/// This is a different mechanism from the monitor-side DDC/CI route, and deliberately so.
/// DDC/CI only works when Windows hands the process a physical-monitor handle, which it
/// does not in every session; the driver ramp always works where the driver does, and it is
/// what NVIDIA's own panel drives. Both routes write to the same sliders, so this one is
/// preferred and DDC/CI is left to the monitor controls it is actually for.
///
/// The driver stores the three attributes in units where <see cref="Neutral"/> is untouched
/// and the extremes sit either side, and remembers them per display under a name derived
/// from the display's LUID. Reading is therefore a registry read, not a driver query.
/// </summary>
internal static class NvidiaDesktopColorService
{
    /// <summary>Darkest the driver will go for brightness.</summary>
    public const float MinSetting = 80f;

    /// <summary>Brightest the driver will go for brightness.</summary>
    public const float MaxSetting = 120f;

    /// <summary>The untouched value, which is where the driver leaves a display by default.</summary>
    public const float NeutralSetting = 100f;

    /// <summary>
    /// First registry value index for the colour controls. The nine values that follow are
    /// brightness, contrast and gamma, each as red, green and blue.
    /// </summary>
    private const int RegistryIndexBase = 3538946;

    private const int RampEntries = NativeMethods.NvApiGamma.RampEntries;

    /// <summary>
    /// The three driver colour attributes, in the driver's own units
    /// (<see cref="MinSetting"/>..<see cref="MaxSetting"/>).
    /// </summary>
    public readonly record struct Settings(float Brightness, float Contrast, float Gamma)
    {
        public static Settings Neutral => new(NeutralSetting, NeutralSetting, NeutralSetting);
    }

    /// <summary>True when this driver exposes the desktop colour controls at all.</summary>
    public static bool IsAvailable => NativeMethods.NvApiGamma.IsAvailable;

    // ── unit conversion ───────────────────────────────────────────────────

    /// <summary>
    /// Converts a 0.0-1.0 slider fraction to driver units, where 0.5 is neutral. The panel
    /// shows a centred slider for these so that "untouched" is the middle, which is what
    /// the NVIDIA Control Panel does too.
    /// </summary>
    public static float ToSetting(double fraction)
        => (float)Math.Round(MinSetting + Math.Clamp(fraction, 0.0, 1.0) * (MaxSetting - MinSetting), 1);

    /// <summary>Converts driver units back to the 0.0-1.0 slider fraction.</summary>
    public static double ToFraction(float setting)
        => Math.Clamp((setting - MinSetting) / (MaxSetting - MinSetting), 0.0, 1.0);

    /// <summary>
    /// Converts the panel's gamma multiplier (1.00 neutral) into driver units.
    ///
    /// The driver's ramp applies <c>pow(level, 1 / (gamma / 100))</c>, so scaling the
    /// multiplier straight through makes the setting a literal gamma exponent: a multiplier
    /// of 1.00 lands exactly on the driver's untouched 100, 0.80 becomes 80, and values
    /// outside the driver's own range are clamped rather than extrapolated.
    /// </summary>
    public static float GammaToSetting(double multiplier)
        => Clamp(NeutralSetting * (float)Math.Clamp(multiplier, 0.30, 2.80));

    /// <summary>Converts driver gamma units back to the 1.00-neutral multiplier.</summary>
    public static double GammaToMultiplier(float setting) => setting / NeutralSetting;

    // ── persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the driver's remembered values for a display. A display the driver has never
    /// been asked to colour has no keys at all, which is the untouched case rather than a
    /// failure, so it reads back as neutral.
    /// </summary>
    public static bool TryRead(uint displayId, out Settings settings, Action<string>? log = null)
    {
        settings = Settings.Neutral;

        if (!IsAvailable)
        {
            log?.Invoke($"desktop colour controls unavailable: {NativeMethods.NvApiGamma.UnavailableReason}");
            return false;
        }

        uint luid = NativeMethods.NvApiGamma.GetLuid(displayId);
        if (luid == 0)
        {
            log?.Invoke($"no driver LUID for display 0x{displayId:X8}");
            return false;
        }

        try
        {
            // The driver keeps the three attributes per channel; the panel exposes them as
            // single values, so the channels are averaged on the way in and written to all
            // three on the way out.
            float brightness = ReadAverage(luid, attribute: 0, log);
            float contrast = ReadAverage(luid, attribute: 1, log);
            float gamma = ReadAverage(luid, attribute: 2, log);

            settings = new Settings(brightness, contrast, gamma);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"registry read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static float ReadAverage(uint luid, int attribute, Action<string>? log)
    {
        float sum = 0;
        int found = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            int index = attribute * 3 + channel;
            if (TryReadOne(luid, index, out float value)) { sum += value; found++; }
        }

        if (found == 0)
        {
            log?.Invoke($"no stored value for attribute {attribute} (defaults to neutral)");
            return NeutralSetting;
        }
        return sum / found;
    }

    private static bool TryReadOne(uint luid, int index, out float value)
    {
        value = NeutralSetting;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                $@"Software\NVIDIA Corporation\Global\NVTweak\Devices\{luid}-0\Color");
            // The driver names these values by index, not by a readable label, so the
            // decimal index is the value name.
            if (key?.GetValue(ValueName(RegistryIndexBase + index)) is not int raw) return false;

            // Anything outside the driver's own range means the key is not one of ours.
            if (raw < MinSetting || raw > MaxSetting) return false;
            value = raw;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The registry value name the driver uses for a colour index.</summary>
    private static string ValueName(int index) => index.ToString();

    /// <summary>
    /// Writes the values to the driver and records them so they survive a display change or
    /// a reboot. The registry write is not optional: without it the driver reapplies its
    /// own neutral ramp the next time the display is re-enabled.
    /// </summary>
    public static bool TryWrite(uint displayId, Settings settings, Action<string>? log = null)
    {
        if (!IsAvailable)
        {
            log?.Invoke($"desktop colour controls unavailable: {NativeMethods.NvApiGamma.UnavailableReason}");
            return false;
        }

        uint luid = NativeMethods.NvApiGamma.GetLuid(displayId);
        if (luid == 0)
        {
            log?.Invoke($"no driver LUID for display 0x{displayId:X8}");
            return false;
        }

        try
        {
            var normalised = new Settings(
                Clamp(settings.Brightness),
                Clamp(settings.Contrast),
                Clamp(settings.Gamma));

            // All three channels move together: the panel exposes one value per attribute,
            // and this is how the NVIDIA Control Panel writes them.
            var ramp = new float[RampEntries * 3];
            for (int i = 0; i < RampEntries; i++)
            {
                float value = CalculateRampValue(i, normalised.Brightness, normalised.Contrast, normalised.Gamma);
                ramp[(i * 3) + 0] = value;
                ramp[(i * 3) + 1] = value;
                ramp[(i * 3) + 2] = value;
            }

            if (!NativeMethods.NvApiGamma.SetGammaCorrection(displayId, ramp))
            {
                log?.Invoke("the driver refused the gamma ramp");
                return false;
            }

            Persist(luid, normalised, log);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"desktop colour write failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Persist(uint luid, Settings settings, Action<string>? log)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                $@"Software\NVIDIA Corporation\Global\NVTweak\Devices\{luid}-0\Color");
            if (key is null) return;

            float[] values = { settings.Brightness, settings.Contrast, settings.Gamma };
            for (int attribute = 0; attribute < 3; attribute++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    key.SetValue(ValueName(RegistryIndexBase + attribute * 3 + channel),
                        (int)values[attribute], RegistryValueKind.DWord);
                }
            }

            // Tells the driver to restore this ramp when the display comes back, rather
            // than treating the stored values as leftovers.
            key.SetValue("NvCplGammaSet", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            log?.Invoke($"could not record the values: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static float Clamp(float value)
        => float.IsFinite(value) ? Math.Clamp(value, MinSetting, MaxSetting) : NeutralSetting;

    // ── the ramp itself ───────────────────────────────────────────────────

    /// <summary>
    /// Builds one entry of the gamma ramp, reproducing the driver's own arithmetic so the
    /// result is indistinguishable from the NVIDIA Control Panel at the same value.
    ///
    /// Contrast pivots about the midpoint: below neutral it squeezes the range inward, above
    /// it stretches the range outward. Brightness then shifts that range, and gamma warps it
    /// around 1.00. Everything is clamped into 0..1 because the ramp is a lookup table of
    /// output levels, not a place for values outside the display's range.
    /// </summary>
    public static float CalculateRampValue(int index, float brightness, float contrast, float gamma)
    {
        double position = (double)index / (RampEntries - 1) - 0.5;

        double c = (contrast - NeutralSetting) / NeutralSetting;
        c = c <= 0.0
            ? (c + 1.0) * position
            : position / (1.0 - c);

        double b = (brightness - NeutralSetting) / NeutralSetting + c + 0.5;
        b = Math.Clamp(b, 0.0, 1.0);

        // A non-finite value here would propagate as NaN rather than being clamped, and a
        // NaN anywhere in a 3072-entry ramp is a corrupt table as far as the driver is
        // concerned. Guard the division and the exponent, not just the endpoints.
        if (!double.IsFinite(b)) b = NeutralFraction;
        if (!double.IsFinite(gamma) || gamma <= 0.0) gamma = NeutralSetting;

        double g = Math.Pow(b, 1.0 / (gamma / NeutralSetting));
        return double.IsFinite(g) ? (float)Math.Clamp(g, 0.0, 1.0) : 0.5f;
    }

    /// <summary>The fraction that corresponds to the driver's untouched value.</summary>
    private const double NeutralFraction = 0.5;
}
