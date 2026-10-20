using System;

namespace kaliteConfig.GpuOverclock.Models
{

    public enum OverclockErrorKind
    {
        GpuNotDetected,

        GpuNotNvidia,

        NvApiInitFailed,

        ControlUnsupported,

        WriteRejected,

        ReadbackMismatch,

        DriverReset,

        GpuDisconnected,

        FanControlUnsupported,

        Unknown,
    }

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

    public static class OverclockErrorMessages
    {
        public static string For(OverclockErrorKind kind) => kind switch
        {
            OverclockErrorKind.GpuNotDetected => "No supported GPU was detected on this system.",
            OverclockErrorKind.GpuNotNvidia => "This GPU is not supported by the overclocking module.",
            OverclockErrorKind.NvApiInitFailed => "Could not start the GPU control interface. Your graphics driver may be too old or damaged - reinstall it and try again.",
            OverclockErrorKind.ControlUnsupported => "This control is not supported by your GPU or driver.",
            OverclockErrorKind.WriteRejected => "The driver refused the value even though it is within the supported range. Try a smaller step.",
            OverclockErrorKind.ReadbackMismatch => "The change was applied but the GPU is not reporting the new value. The change may not be in effect.",
            OverclockErrorKind.DriverReset => "The graphics driver recovered from a crash (TDR). Your last change was automatically reverted.",
            OverclockErrorKind.GpuDisconnected => "The GPU disappeared while the module was active (unplugged or re-detection removed it).",
            OverclockErrorKind.FanControlUnsupported => "This GPU does not expose a controllable fan.",
            _ => "An unexpected error occurred. See the change log for details.",
        };
    }
}
