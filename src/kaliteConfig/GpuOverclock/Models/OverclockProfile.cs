using System;
using System.Collections.Generic;

namespace kaliteConfig.GpuOverclock.Models
{
    public enum GpuFanMode
    {
        Auto,

        Static,

        Curve,
    }

    public sealed record FanCurvePoint(int TempC, int FanPercent);

    public sealed class OverclockProfile
    {
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Name { get; set; } = "";
        public Guid Id { get; set; } = Guid.NewGuid();

        public int? CoreOffsetMHz { get; set; }
        public int? MemOffsetMHz { get; set; }
        public double? PowerLimitPercent { get; set; }
        public int? TempLimitC { get; set; }

        public List<int>? VfCurveOffsets { get; set; }

        public int? GlobalVoltageBoostOffsetMHz { get; set; }

        public GpuFanMode FanMode { get; set; } = GpuFanMode.Auto;
        public int? FanStaticPercent { get; set; }
        public List<FanCurvePoint> FanCurvePoints { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastAppliedAt { get; set; }

        public DateTime? ConfirmedAt { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasConfirmedAt => ConfirmedAt is not null;

        public bool HasBeenManuallyValidated { get; set; }

        public DateTime? LastValidatedAt { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsStartupDefault { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (CoreOffsetMHz is { } c && c != 0) parts.Add($"core {c:+0;-0;0} MHz");
                if (MemOffsetMHz is { } m && m != 0) parts.Add($"mem {m:+0;-0;0} MHz");
                if (PowerLimitPercent is { } p) parts.Add($"power {p:0.#}%");
                if (TempLimitC is { } t) parts.Add($"temp {t} °C");
                if (VfCurveOffsets is { Count: > 0 }) parts.Add($"V/F curve ({VfCurveOffsets.Count} pts)");
                else if (GlobalVoltageBoostOffsetMHz is { } v && v != 0) parts.Add($"V/F {v:+0;-0;0} MHz");
                parts.Add(FanMode switch
                {
                    GpuFanMode.Static => $"fan {FanStaticPercent}%",
                    GpuFanMode.Curve => $"fan curve ({FanCurvePoints.Count} pts)",
                    _ => "fan auto",
                });
                return string.Join(", ", parts);
            }
        }
    }

    public enum OverclockChangeSource
    {
        Manual,
        ProfileApply,
        StartupApply,
        AutoRevert,

        GameAutoApply,
    }

    public enum OverclockChangeResult
    {
        Success,
        Reverted,
        Failed,
    }

    public sealed record AppliedChangeLogEntry
    {
        public required DateTime Timestamp { get; init; }
        public required string ControlName { get; init; }
        public required string OldValue { get; init; }
        public required string NewValue { get; init; }
        public required OverclockChangeSource Source { get; init; }
        public required OverclockChangeResult Result { get; init; }
        public string? FailureReason { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string TimestampDisplay => Timestamp.ToString("HH:mm:ss");

        [System.Text.Json.Serialization.JsonIgnore]
        public string ResultDisplay => Result switch
        {
            OverclockChangeResult.Success => "OK",
            OverclockChangeResult.Reverted => "REVERTED",
            _ => "FAILED",
        };

        [System.Text.Json.Serialization.JsonIgnore]
        public string LineDisplay
        {
            get
            {
                var line = $"{ControlName}: {OldValue}";
                if (!string.Equals(OldValue, NewValue, StringComparison.Ordinal)) line += $" -> {NewValue}";
                line += $"  ({Source})";
                if (!string.IsNullOrEmpty(FailureReason)) line += $" - {FailureReason}";
                return line;
            }
        }
    }
}
