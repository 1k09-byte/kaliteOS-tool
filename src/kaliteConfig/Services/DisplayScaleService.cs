// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static kaliteConfig.Native.NativeMethods.Display;

namespace kaliteConfig.Services;

/// <summary>
/// Reads and writes a monitor's per-source DPI scale — the value Windows shows as
/// "Scale" in display settings.
///
/// Scale is owned by the *source*, not the target, and the OS only accepts it as a step
/// count relative to the scale it recommends for that panel (resolution, physical size
/// and viewing distance). So "150%" is stored as "two steps above recommended", and the
/// recommended value has to be read before any write can be converted. Both calls go
/// through the undocumented DPI-scale device-info types that the Settings app itself uses;
/// every other route needs a sign-out.
/// </summary>
internal static class DisplayScaleService
{
    /// <summary>
    /// The percentages Windows offers, in the order its own scale list uses. The index
    /// difference between two of them is exactly one scale step.
    /// </summary>
    private static readonly int[] DpiValues = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// <summary>Everything the UI needs to present the scale picker for one display.</summary>
    internal readonly record struct ScaleInfo(
        int CurrentPercent,
        int RecommendedPercent,
        int MinimumPercent,
        int MaximumPercent,
        bool IsKnown)
    {
        public static ScaleInfo Unknown => new(100, 100, 100, 100, false);
    }

    /// <summary>
    /// Reads the scale range and current value for one source. Never throws: an OS that
    /// refuses the packet yields <see cref="ScaleInfo.Unknown"/> rather than an exception.
    /// </summary>
    public static ScaleInfo Read(LUID sourceAdapterId, uint sourceId)
    {
        var packet = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetDpiScale,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                AdapterId = sourceAdapterId,
                Id = sourceId,
            },
        };

        int hr;
        try { hr = DisplayConfigGetDeviceInfo(ref packet); }
        catch (Exception) { return ScaleInfo.Unknown; }
        if (hr != 0) return ScaleInfo.Unknown;

        return FromScaleSteps(packet.MinScaleRel, packet.CurScaleRel, packet.MaxScaleRel);
    }

    /// <summary>
    /// Turns the OS's three step counts into percentages. The step counts are relative to
    /// the scale the OS recommends for the panel, and index into <see cref="DpiValues"/>
    /// from an offset given by the minimum, so the minimum is always 100% and its own
    /// step count says how far the recommendation sits above that.
    ///
    /// This is the whole of the scale arithmetic, split out from the P/Invoke so the
    /// conversion can be checked without a display attached.
    /// </summary>
    internal static ScaleInfo FromScaleSteps(int minScaleRel, int curScaleRel, int maxScaleRel)
    {
        // The OS can report a current step outside the range it just reported as the
        // bounds, so it is clamped the same way the Settings app clamps it.
        int cur = Math.Clamp(curScaleRel, minScaleRel, maxScaleRel);

        int minAbs = Math.Abs(minScaleRel);
        if (minAbs + maxScaleRel + 1 > DpiValues.Length) return ScaleInfo.Unknown;
        if (minAbs + cur < 0 || minAbs + cur >= DpiValues.Length) return ScaleInfo.Unknown;

        return new ScaleInfo(
            DpiValues[minAbs + cur],
            DpiValues[minAbs],
            DpiValues[0],
            DpiValues[minAbs + maxScaleRel],
            IsKnown: true);
    }

    /// <summary>
    /// The step count to send for a target percentage, relative to the recommended value.
    /// Null when the OS scale list does not contain that percentage, which is a genuine
    /// "cannot be set" rather than a value to approximate.
    /// </summary>
    internal static int? StepForPercent(ScaleInfo info, int percent)
    {
        if (!info.IsKnown) return null;

        int targetIndex = Array.IndexOf(DpiValues, Math.Clamp(percent, info.MinimumPercent, info.MaximumPercent));
        int recommendedIndex = Array.IndexOf(DpiValues, info.RecommendedPercent);
        if (targetIndex < 0 || recommendedIndex < 0) return null;

        return targetIndex - recommendedIndex;
    }

    /// <summary>
    /// Sets the scale for one source. Returns false when the OS refused the change, which
    /// is the honest answer rather than reporting a scale the desktop never applied.
    /// </summary>
    public static bool Write(LUID sourceAdapterId, uint sourceId, int percent)
    {
        var info = Read(sourceAdapterId, sourceId);
        if (!info.IsKnown) return false;
        if (percent == info.CurrentPercent) return true;

        int? step = StepForPercent(info, percent);
        if (step is null) return false;

        var packet = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SetDpiScale,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                AdapterId = sourceAdapterId,
                Id = sourceId,
            },
            ScaleRel = step.Value,
        };

        try { return DisplayConfigSetDeviceInfo(ref packet) == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The percentages selectable for a panel, i.e. the OS list trimmed to what this
    /// display actually supports.
    /// </summary>
    public static IReadOnlyList<int> AvailableScales(ScaleInfo info) =>
        info.IsKnown
            ? DpiValues.Where(v => v >= info.MinimumPercent && v <= info.MaximumPercent).ToArray()
            : DpiValues;
}
