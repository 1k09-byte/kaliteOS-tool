using System;
using System.Collections.Generic;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>How the fan loop behaves when a profile with fan settings is applied.</summary>
    public enum GpuFanMode
    {
        /// <summary>Driver default — an explicit restore call, never "just stop writing".</summary>
        Auto,

        /// <summary>Fixed percentage, one write.</summary>
        Static,

        /// <summary>Temperature-driven curve evaluated by FanCurveExecutionService.</summary>
        Curve,
    }

    /// <summary>One point of the fan curve: at this temp, run this %.</summary>
    public sealed record FanCurvePoint(int TempC, int FanPercent);

    /// <summary>
    /// Named overclock profile. Schema version on save so future fields don't
    /// break previously saved JSON (ProfileStorageService).
    /// </summary>
    public sealed class OverclockProfile
    {
        /// <summary>Bump when the persisted shape changes; loader migrates/ignores unknowns.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Name { get; set; } = "";
        public Guid Id { get; set; } = Guid.NewGuid();

        public int? CoreOffsetMHz { get; set; }
        public int? MemOffsetMHz { get; set; }
        public double? PowerLimitPercent { get; set; }
        public int? TempLimitC { get; set; }

        public GpuFanMode FanMode { get; set; } = GpuFanMode.Auto;
        public int? FanStaticPercent { get; set; }
        public List<FanCurvePoint> FanCurvePoints { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastAppliedAt { get; set; }

        /// <summary>
        /// Set when a profile's batch survived its confirmation window (or was
        /// re-applied identically after a prior confirm). A profile with a
        /// non-null ConfirmedAt is "pre-boot validated": startup reapply may
        /// skip the interactive countdown (see OverclockViewModel).
        /// Not exposed in the UI as an editable field.
        /// </summary>
        public DateTime? ConfirmedAt { get; set; }

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

    /// <summary>Why a change was made — the audit trail's "who asked for this".</summary>
    public enum OverclockChangeSource
    {
        Manual,
        ProfileApply,
        StartupApply,
        AutoRevert,
    }

    /// <summary>Outcome of a logged change.</summary>
    public enum OverclockChangeResult
    {
        Success,
        Reverted,
        Failed,
    }

    /// <summary>One audited change (what this app actually touched, and when).</summary>
    public sealed record AppliedChangeLogEntry
    {
        public required DateTime Timestamp { get; init; }
        public required string ControlName { get; init; }
        public required string OldValue { get; init; }
        public required string NewValue { get; init; }
        public required OverclockChangeSource Source { get; init; }
        public required OverclockChangeResult Result { get; init; }
        public string? FailureReason { get; init; }

        // Display-only projections for the in-app log viewer (not serialized).
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
                if (!string.IsNullOrEmpty(FailureReason)) line += $" — {FailureReason}";
                return line;
            }
        }
    }
}
