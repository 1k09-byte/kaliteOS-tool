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

    public enum Radeon3DScope
    {
        PerGpu,

        DisplayLevel,
    }

    public enum Radeon3DParameter
    {
        None,

        MinFps,

        MaxFps,

        TargetFps,

        Sharpness,

        Resolution,
    }

    public sealed record Radeon3DFeatureInfo(
        Radeon3DSetting Setting,
        string Title,
        string Glyph,
        string Summary,
        Radeon3DScope Scope,
        Radeon3DParameter Parameter,
        string ParameterLabel)
    {
        public bool HasParameter => Parameter != Radeon3DParameter.None;
    }

    public static class Radeon3DCatalog
    {
        public static readonly IReadOnlyList<Radeon3DFeatureInfo> All = new[]
        {
            new Radeon3DFeatureInfo(
                Radeon3DSetting.AntiLag,
                "Radeon Anti-Lag",
                ((char)0xE7E8).ToString(),
                "Driver-level frame pacing. Lowers input latency by syncing the CPU " +
                "to the GPU queue. Not to be confused with Anti-Lag+/Anti-Lag Next, " +
                "which patches game files and is not offered here.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.None,
                string.Empty),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.Boost,
                "Radeon Boost",
                ((char)0xE9D2).ToString(),
                "Renders below native resolution and upscales the result, holding the " +
                "frame rate. Cannot run at the same time as Chill.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.Resolution,
                "Minimum resolution"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.Chill,
                "Radeon Chill",
                ((char)0xE7F4).ToString(),
                "Caps the frame rate to a ceiling you set, and slows the card down " +
                "when it would otherwise exceed it. Cannot run at the same time as " +
                "Boost or Anti-Lag.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.MinFps,
                "Minimum FPS"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.ImageSharpening,
                "Radeon Image Sharpening",
                ((char)0xE8B5).ToString(),
                "Edge-aware sharpening applied after upscaling or at native resolution, " +
                "to recover detail lost to upscaling.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.Sharpness,
                "Sharpness"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.EnhancedSync,
                "Enhanced Sync",
                ((char)0xE777).ToString(),
                "Frames are only presented when the GPU has finished them, removing " +
                "tearing without adding input lag. Carries a small cost in frame pacing.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.None,
                string.Empty),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.FrameRateTargetControl,
                "Frame Rate Target Control",
                ((char)0xE9D9).ToString(),
                "Caps the frame rate at a target you choose, by clocking down rather " +
                "than by dropping frames the way VSync does.",
                Radeon3DScope.PerGpu,
                Radeon3DParameter.TargetFps,
                "Target FPS"),

            new Radeon3DFeatureInfo(
                Radeon3DSetting.RadeonSuperResolution,
                "Radeon Super Resolution",
                ((char)0xE9A1).ToString(),
                "Spatial upscaling inside the driver, applied to the whole desktop. " +
                "ADLX exposes this one per DISPLAY, not per GPU, so it is not a " +
                "property of the adapter you have selected above.",
                Radeon3DScope.DisplayLevel,
                Radeon3DParameter.Sharpness,
                "Sharpness"),
        };

        public static Radeon3DFeatureInfo Info(Radeon3DSetting setting)
        {
            foreach (Radeon3DFeatureInfo info in All)
            {
                if (info.Setting == setting) return info;
            }
            throw new System.ArgumentOutOfRangeException(nameof(setting), setting, "Not a Radeon 3D feature.");
        }
    }

    public static class RadeonResult
    {
        public const int Ok = 0;
        public const int AlreadyEnabled = 1;
        public const int AlreadyInitialized = 2;

        public const int AdlxInvalidObject = 10;

        public static bool Succeeded(int result)
            => result is Ok or AlreadyEnabled or AlreadyInitialized;

        public static string Describe(int result) => result switch
        {
            Ok => "ADLX_OK",
            AlreadyEnabled => "ADLX_ALREADY_ENABLED",
            AlreadyInitialized => "ADLX_ALREADY_INITIALIZED",
            3 => "ADLX_FAIL",
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
