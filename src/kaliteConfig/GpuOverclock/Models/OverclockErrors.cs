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
using System;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>Identifies which hardware element a telemetry/control capability refers to.</summary>
    public enum GpuControlKind
    {
        CoreClockOffset,
        MemoryClockOffset,
        PowerLimit,
        TempLimit,
        FanSpeed,
    }

    /// <summary>
    /// Distinct failure modes the module recognizes. Each maps to a specific,
    /// user-legible message - no raw exception text is surfaced in the UI.
    /// The taxonomy deliberately mirrors NVIDIAApiException.Status cases we can
    /// actually see from NVAPI, plus module-level states.
    /// </summary>
    public enum OverclockErrorKind
    {
        /// <summary>No NVIDIA discrete GPU present (or only WDDM 1.0-era adapters).</summary>
        GpuNotDetected,

        /// <summary>GPU present but not NVIDIA - module is NVIDIA-only by spec.</summary>
        GpuNotNvidia,

        /// <summary>NvAPIWrapper initialize failed: nvapi library missing/mismatched, driver too old.</summary>
        NvApiInitFailed,

        /// <summary>Control is unsupported on this GPU/driver (no range, NotSupported status).</summary>
        ControlUnsupported,

        /// <summary>Write rejected by driver although the value was within the queried range.</summary>
        WriteRejected,

        /// <summary>Write succeeded but telemetry/readback does not confirm the new value.</summary>
        ReadbackMismatch,

        /// <summary>Driver reset (TDR) detected during/after a write window.</summary>
        DriverReset,

        /// <summary>GPU disappeared mid-session (re-detect removed it, eGPU unplug).</summary>
        GpuDisconnected,

        /// <summary>Fan control unsupported / cooler is not controllable on this board.</summary>
        FanControlUnsupported,

        /// <summary>Unexpected failure - details in the change log, message kept generic.</summary>
        Unknown,
    }

    /// <summary>
    /// Result wrapper for controller operations. Expected failure modes are
    /// returned as values - exceptions are reserved for genuinely exceptional
    /// conditions (OOM, thread aborts).
    /// </summary>
    public class GpuResult
    {
        public bool IsSuccess { get; }
        public OverclockErrorKind ErrorKind { get; }
        public string? Detail { get; }

        protected GpuResult(bool success, OverclockErrorKind kind, string? detail)
        {
            IsSuccess = success;
            ErrorKind = kind;
            Detail = detail;
        }

        public static GpuResult Ok() => new(true, OverclockErrorKind.Unknown, null);
        public static GpuResult Fail(OverclockErrorKind kind, string? detail = null) => new(false, kind, detail);
    }

    public sealed class GpuResult<T> : GpuResult
    {
        public T? Value { get; }

        private GpuResult(T value) : base(true, OverclockErrorKind.Unknown, null) => Value = value;
        private GpuResult(OverclockErrorKind kind, string? detail) : base(false, kind, detail) { }

        public static GpuResult<T> Ok(T value) => new(value);
        public static new GpuResult<T> Fail(OverclockErrorKind kind, string? detail = null) => new(kind, detail);
    }

    /// <summary>Human-legible message for each error kind (UI-facing, no exception text).</summary>
    public static class OverclockErrorMessages
    {
        public static string For(OverclockErrorKind kind) => kind switch
        {
            OverclockErrorKind.GpuNotDetected => "No NVIDIA GPU was detected on this system.",
            OverclockErrorKind.GpuNotNvidia => "The detected GPU is not NVIDIA - this module supports NVIDIA GPUs only.",
            OverclockErrorKind.NvApiInitFailed => "Could not start the NVIDIA control interface (nvapi). Your driver may be too old or damaged - reinstall the NVIDIA driver and try again.",
            OverclockErrorKind.ControlUnsupported => "This control is not supported by your GPU or driver.",
            OverclockErrorKind.WriteRejected => "The driver refused the value even though it is within the supported range. Try a smaller step.",
            OverclockErrorKind.ReadbackMismatch => "The change was applied but the GPU is not reporting the new value. The change may not be in effect.",
            OverclockErrorKind.DriverReset => "The graphics driver recovered from a crash (TDR). Your last change was automatically reverted.",
            OverclockErrorKind.GpuDisconnected => "The GPU disappeared while the module was active (unplugged or re-detection removed it).",
            OverclockErrorKind.FanControlUnsupported => "This GPU does not expose a controllable fan through the NVIDIA interface.",
            _ => "An unexpected error occurred. See the change log for details.",
        };
    }
}
