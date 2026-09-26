// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>How the fan loop behaves when a profile with fan settings is applied.</summary>
    public enum GpuFanMode
    {
        /// <summary>Driver default - an explicit restore call, never "just stop writing".</summary>
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
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Name { get; set; } = "";
        public Guid Id { get; set; } = Guid.NewGuid();

        public int? CoreOffsetMHz { get; set; }
        public int? MemOffsetMHz { get; set; }
        public double? PowerLimitPercent { get; set; }
        public int? TempLimitC { get; set; }

        /// <summary>
        /// Per-point V/F curve offsets in driver point order (v2). Null/empty
        /// means "this profile doesn't touch the curve". Lengths are matched
        /// against the live curve on apply - a count mismatch skips the curve
        /// portion rather than writing misaligned points.
        /// </summary>
        public List<int>? VfCurveOffsets { get; set; }

        /// <summary>
        /// Simple-mode flat V/F offset (v2). UI convenience only: when set and
        /// VfCurveOffsets is absent, apply expands it uniformly across all
        /// points (clamped per-point). When both are present, the per-point
        /// table wins.
        /// </summary>
        public int? GlobalVoltageBoostOffsetMHz { get; set; }

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

        /// <summary>UI projection: this profile survived a confirmation window (pre-boot validated).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasConfirmedAt => ConfirmedAt is not null;

        /// <summary>
        /// Auto-switch eligibility (v2, per-game profiles): true only after
        /// this profile was manually applied AND confirmed by the user through
        /// the normal interactive safety state machine in a plain desktop
        /// context - never set by startup reapply or game auto-apply paths.
        /// A profile that was never manually validated can never auto-apply.
        /// </summary>
        public bool HasBeenManuallyValidated { get; set; }

        /// <summary>When the manual validation above happened.</summary>
        public DateTime? LastValidatedAt { get; set; }

        /// <summary>
        /// UI projection set by ProfileStorageService.LoadAll: this profile is
        /// the one designated for startup reapply. Not serialized.
        /// </summary>
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

    /// <summary>Why a change was made - the audit trail's "who asked for this".</summary>
    public enum OverclockChangeSource
    {
        Manual,
        ProfileApply,
        StartupApply,
        AutoRevert,

        /// <summary>
        /// Unattended per-game auto-switch. Appended last so the log viewer's
        /// index-based filter mapping for the earlier values never shifts.
        /// </summary>
        GameAutoApply,
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
                if (!string.IsNullOrEmpty(FailureReason)) line += $" - {FailureReason}";
                return line;
            }
        }
    }
}
