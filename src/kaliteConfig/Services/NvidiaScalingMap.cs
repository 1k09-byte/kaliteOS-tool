// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using kaliteConfig.Models;
using ScalingMode = NvAPIWrapper.Native.Display.Scaling;

namespace kaliteConfig.Services;

/// <summary>
/// Translates between the two scaling dropdowns and NvAPI's single flat scaling value.
///
/// NvAPI has no two-dimensional model. It exposes one value that encodes both "which
/// device does the scaling" and "how is it stretched", plus one value,
/// <see cref="ScalingMode.Customized"/>, that means "driver decides, leave the path
/// alone". Writing that sentinel is a silent no-op — the path comes back untouched and
/// reading it reports something else entirely — so it is never a write target here.
///
/// Not every location/mode pair is therefore expressible: the GPU can only scan out
/// aspect-preserving or at native resolution, never stretched and never in whole-pixel
/// steps. <see cref="ModesFor"/> is the single source of truth for what the UI may
/// offer, and <see cref="TryToDriver"/> fails loudly for a pair the driver has no value
/// for rather than substituting one the user did not ask for.
///
/// Every pair <see cref="ModesFor"/> offers round-trips exactly through
/// <see cref="FromDriver"/>, which is what lets Apply tell "the driver already matches"
/// apart from "the user changed something".
/// </summary>
public static class NvidiaScalingMap
{
    private static readonly Dictionary<NvidiaScalingMode, string> Labels = new()
    {
        [NvidiaScalingMode.AspectRatio] = "Aspect ratio",
        [NvidiaScalingMode.Full] = "Full-screen",
        [NvidiaScalingMode.NoScaling] = "No scaling",
        [NvidiaScalingMode.Integer] = "Integer scaling",
    };

    /// <summary>
    /// The modes NVAPI can actually perform for <paramref name="location"/>, in list
    /// order. GPU scaling only has aspect-preserving and native scan-out values, so
    /// full-screen stretch and integer steps are not offered there at all.
    /// </summary>
    public static IReadOnlyList<NvidiaScalingMode> ModesFor(NvidiaScalingLocation location) => location switch
    {
        NvidiaScalingLocation.Gpu =>
            new[] { NvidiaScalingMode.AspectRatio, NvidiaScalingMode.NoScaling },
        _ => new[]
        {
            NvidiaScalingMode.AspectRatio,
            NvidiaScalingMode.Full,
            NvidiaScalingMode.NoScaling,
            NvidiaScalingMode.Integer,
        },
    };

    /// <summary>Display name for one mode, matching the NVIDIA Control Panel's wording.</summary>
    public static string LabelFor(NvidiaScalingMode mode) =>
        Labels.TryGetValue(mode, out var label) ? label : mode.ToString();

    /// <summary>
    /// Reads a driver value as a location plus a mode.
    ///
    /// The two GPUScanOut* values are the only ones the GPU performs, so they decide the
    /// location; the rest are the monitor's. <see cref="ScalingMode.Default"/>,
    /// <see cref="ScalingMode.Customized"/> and anything this wrapper does not recognise
    /// report as display/aspect-ratio, the driver's own default shape — the dropdowns
    /// cannot show "unknown" without hiding a control that mostly works.
    /// </summary>
    public static (NvidiaScalingLocation Location, NvidiaScalingMode Mode) FromDriver(ScalingMode value) => value switch
    {
        ScalingMode.ToClosest => (NvidiaScalingLocation.Display, NvidiaScalingMode.Full),
        ScalingMode.ToNative => (NvidiaScalingLocation.Display, NvidiaScalingMode.NoScaling),
        ScalingMode.GPUScanOutToNative => (NvidiaScalingLocation.Gpu, NvidiaScalingMode.NoScaling),
        ScalingMode.ToAspectScanOutToClosest => (NvidiaScalingLocation.Display, NvidiaScalingMode.AspectRatio),
        ScalingMode.ToAspectScanOutToNative => (NvidiaScalingLocation.Display, NvidiaScalingMode.Integer),
        ScalingMode.GPUScanOutToClosest => (NvidiaScalingLocation.Gpu, NvidiaScalingMode.AspectRatio),
        _ => (NvidiaScalingLocation.Display, NvidiaScalingMode.AspectRatio),
    };

    /// <summary>
    /// Maps a location plus a mode onto the driver value that expresses it.
    ///
    /// Returns false for a pair the driver cannot express, leaving
    /// <paramref name="value"/> at the driver's own default rather than at anything that
    /// would look like a successful write.
    /// </summary>
    public static bool TryToDriver(NvidiaScalingLocation location, NvidiaScalingMode mode, out ScalingMode value)
    {
        switch (location, mode)
        {
            case (NvidiaScalingLocation.Display, NvidiaScalingMode.AspectRatio):
                value = ScalingMode.ToAspectScanOutToClosest;
                return true;
            case (NvidiaScalingLocation.Display, NvidiaScalingMode.Full):
                value = ScalingMode.ToClosest;
                return true;
            case (NvidiaScalingLocation.Display, NvidiaScalingMode.NoScaling):
                value = ScalingMode.ToNative;
                return true;
            case (NvidiaScalingLocation.Display, NvidiaScalingMode.Integer):
                value = ScalingMode.ToAspectScanOutToNative;
                return true;
            case (NvidiaScalingLocation.Gpu, NvidiaScalingMode.AspectRatio):
                value = ScalingMode.GPUScanOutToClosest;
                return true;
            case (NvidiaScalingLocation.Gpu, NvidiaScalingMode.NoScaling):
                value = ScalingMode.GPUScanOutToNative;
                return true;
            default:
                value = ScalingMode.Default;
                return false;
        }
    }

    /// <summary>
    /// True when the driver reported a value the table above does not describe. Scaling
    /// still works; the panel just cannot claim to know which of the two dropdowns the
    /// driver is in, so it says so instead of guessing.
    /// </summary>
    public static bool IsKnownDriverValue(ScalingMode value) => value switch
    {
        ScalingMode.ToClosest => true,
        ScalingMode.ToNative => true,
        ScalingMode.GPUScanOutToNative => true,
        ScalingMode.ToAspectScanOutToClosest => true,
        ScalingMode.ToAspectScanOutToNative => true,
        ScalingMode.GPUScanOutToClosest => true,
        _ => false,
    };
}
