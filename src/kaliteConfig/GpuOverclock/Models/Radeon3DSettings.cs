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
using System.Collections.Generic;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// The Adrenalin-style 3D features this page drives. Pure vocabulary: the
    /// ADLX plumbing that implements it lives in
    /// <c>GpuOverclock\Services\AmdAdlxInterop.3D.cs</c>, and the behaviour
    /// rules live in <see cref="Radeon3DPolicy"/>.
    ///
    /// Deliberately absent: Anti-Lag+ / Anti-Lag Next. AMD ships those as
    /// <c>IADLX3DAntiLag1</c> (GetLevel/SetLevel over ADLX_ANTILAG_STATE), but
    /// they are a separate per-game file-patch feature that has drawn VAC and
    /// other anti-cheat bans, so this page does not touch them.
    /// </summary>
    public enum Radeon3DSetting
    {
        AntiLag,
        Boost,
        Chill,
        ImageSharpening,
        EnhancedSync,
        FrameRateTargetControl,
        RadeonSuperResolution,
    }

    /// <summary>How a feature is scoped, which decides how the row is labelled.</summary>
    public enum Radeon3DScope
    {
        /// <summary>Adlx 3D settings services take an IADLXGPU handle for this one.</summary>
        PerGpu,

        /// <summary>The ADLX getter takes no GPU handle: the setting is display-level.</summary>
        DisplayLevel,
    }

    /// <summary>What kind of value a feature's parameter is, if it has one.</summary>
    public enum Radeon3DParameter
    {
        None,

        /// <summary>Chill: a minimum FPS floor.</summary>
        MinFps,

        /// <summary>Chill: a maximum FPS ceiling.</summary>
        MaxFps,

        /// <summary>Frame Rate Target Control: the target frame rate.</summary>
        TargetFps,

        /// <summary>Radeon Super Resolution / Image Sharpening: an intensity.</summary>
        Sharpness,

        /// <summary>Boost: the minimum render resolution.</summary>
        Resolution,
    }

    /// <summary>Static description of one feature row. No driver state in here.
    ///
    /// The glyphs are Segoe Fluent Icons codepoints expressed as char casts
    /// rather than "\uXXXX" escapes on purpose: an escape that gets literalized
    /// on the way through a tool or an editor renders as an empty box, and a
    /// char cast cannot. Every codepoint here is one the app already uses, so
    /// all of them are known to exist in the installed font.</summary>
    public sealed record Radeon3DFeatureInfo(
        Radeon3DSetting Setting,
        string Title,
        string Glyph,
        string Summary,
        Radeon3DScope Scope,
        Radeon3DParameter Parameter,
        string ParameterLabel)
    {
        /// <summary>True when this row exposes a numeric parameter under the toggle.</summary>
        public bool HasParameter => Parameter != Radeon3DParameter.None;
    }

    /// <summary>
    /// The catalogue behind the Radeon tab. One entry per feature, in the order
    /// they appear on the page.
    /// </summary>
    public static class Radeon3DCatalog
    {
        public static readonly IReadOnlyList<Radeon3DFeatureInfo> All = new[]
        {
            new Radeon3DFeatureInfo(
                Radeon3DSetting.AntiLag,
                "Radeon Anti-Lag",
                ((char)0xE7E8).ToString(),  // codepoint already used by the Settings page
                "Driver-level frame pacing. Lowers input latency by syncing the CPU " +
                "to the GPU queue. Not to be confused with Anti-Lag+/Anti-Lag Next, " +
                "which patches game files and is not offered here.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.None,
                string.Empty),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.Boost,
                "Radeon Boost",
                ((char)0xE9D2).ToString(),  // codepoint already used elsewhere in the app
                "Renders below native resolution and upscales the result, holding the " +
                "frame rate. Cannot run at the same time as Chill.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.Resolution,
                "Minimum resolution"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.Chill,
                "Radeon Chill",
                ((char)0xE7F4).ToString(),  // codepoint already used elsewhere in the app
                "Caps the frame rate to a ceiling you set, and slows the card down " +
                "when it would otherwise exceed it. Cannot run at the same time as " +
                "Boost or Anti-Lag.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.MinFps,
                "Minimum FPS"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.ImageSharpening,
                "Radeon Image Sharpening",
                ((char)0xE8B5).ToString(),  // codepoint already used elsewhere in the app
                "Edge-aware sharpening applied after upscaling or at native resolution, " +
                "to recover detail lost to upscaling.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.Sharpness,
                "Sharpness"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.EnhancedSync,
                "Enhanced Sync",
                ((char)0xE777).ToString(),  // codepoint already used elsewhere in the app
                "Frames are only presented when the GPU has finished them, removing " +
                "tearing without adding input lag. Carries a small cost in frame pacing.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.None,
                string.Empty),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.FrameRateTargetControl,
                "Frame Rate Target Control",
                ((char)0xE9D9).ToString(),  // codepoint already used elsewhere in the app
                "Caps the frame rate at a target you choose, by clocking down rather " +
                "than by dropping frames the way VSync does.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.TargetFps,
                "Target FPS"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.RadeonSuperResolution,
                "Radeon Super Resolution",
                ((char)0xE9A1).ToString(),  // codepoint already used by the NVIDIA page header
                "Spatial upscaling inside the driver, applied to the whole desktop. " +
                "ADLX exposes this one per DISPLAY, not per GPU, so it is not a " +
                "property of the adapter you have selected above.",
                Radeon3DScope.DisplayLevel,
                Radeon3DParameter.Sharpness,
                "Sharpness"),
        };

        /// <summary>Looks a feature up by its enum value.</summary>
        public static Radeon3DFeatureInfo Info(Radeon3DSetting setting)
        {
            foreach (Radeon3DFeatureInfo info in All)
            {
                if (info.Setting == setting) return info;
            }
            throw new System.ArgumentOutOfRangeException(nameof(setting), setting, "Not a Radeon 3D feature.");
        }
    }

    /// <summary>
    /// ADLX_RESULT, spelled out. Every write path in the Radeon tab reports the
    /// raw code the driver returned, and an "already enabled" answer is a success
    /// rather than a failure - the headers' own ADLX_SUCCEEDED treats 0, 1 and 2
    /// as success.
    /// </summary>
    public static class RadeonResult
    {
        /// <summary>ADLX_SUCCEEDED: ADLX_OK, ADLX_ALREADY_ENABLED, ADLX_ALREADY_INITIALIZED.</summary>
        public const int Ok = 0;
        public const int AlreadyEnabled = 1;
        public const int AlreadyInitialized = 2;

        /// <summary>ADLX_INVALID_OBJECT - our own stand-in when the interface could
        /// not be reached at all, so a missing interface is not confused with a
        /// driver that answered.</summary>
        public const int AdlxInvalidObject = 10;

        public static bool Succeeded(int result)
            => result is Ok or AlreadyEnabled or AlreadyInitialized;

        /// <summary>The header's symbolic name, so the UI can show the real code.</summary>
        public static string Describe(int result) => result switch
        {
            Ok => "ADLX_OK",
            AlreadyEnabled => "ADLX_ALREADY_ENABLED",
            AlreadyInitialized => "ADLX_ALREADY_INITIALIZED",
            3 => "ADLX_FAIL",  // handled by the generic arm below, which names the code
            4 => "ADLX_INVALID_ARGS",
            5 => "ADLX_BAD_VER",
            6 => "ADLX_UNKNOWN_INTERFACE",
            7 => "ADLX_TERMINATED",
            8 => "ADLX_ADL_INIT_ERROR",
            9 => "ADLX_NOT_FOUND",
            AdlxInvalidObject => "ADLX_INVALID_OBJECT",
            11 => "ADLX_ORPHAN_OBJECTS",
            12 => "ADLX_NOT_SUPPORTED",
            13 => "ADLX_PENDING_OPERATION",
            14 => "ADLX_GPU_INACTIVE",
            15 => "ADLX_GPU_IN_USE",
            16 => "ADLX_TIMEOUT_OPERATION",
            17 => "ADLX_NOT_ACTIVE",
            18 => "ADLX_RESET_NEEDED",
            _ => $"ADLX_RESULT({result})",
        };

        /// <summary>One line for the user: what happened, and the real code behind it.</summary>
        public static string Explain(string action, int result) => result switch
        {
            Ok => $"{action} applied.",
            AlreadyEnabled => $"{action} was already on; the driver left it that way.",
            AlreadyInitialized => $"{action} applied ({Describe(result)}).",
            12 => $"{action} was rejected: this driver does not support it on this adapter.",
            15 => $"{action} was rejected: the GPU is in use by a running application. Close games and try again.",
            16 => $"{action} timed out inside the driver.",
            14 => $"{action} was rejected: the GPU is inactive.",
            _ => $"{action} failed: {Describe(result)} ({result}).",
        };
    }
}
