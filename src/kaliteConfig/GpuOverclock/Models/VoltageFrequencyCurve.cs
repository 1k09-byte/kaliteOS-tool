using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// One editable point of the GPU voltage/frequency curve.
    ///
    /// HONESTY NOTE (Ampere/Ada, RTX 30/40): raw millivolt override is locked
    /// at the vBIOS level - there is no NVAPI call that sets true voltage.
    /// What this point edits is the per-point CLOCK OFFSET applied along the
    /// GPU's existing voltage/frequency table: the boost curve shifted within
    /// its factory-defined envelope, never voltage set directly. VoltageMv and
    /// BaseFrequencyMHz are read-only values queried from the driver at
    /// session start; only OffsetMHz is user-editable.
    /// </summary>
    public sealed class VfCurvePoint
    {
        /// <summary>Voltage of this curve point in mV - read-only, from the driver.</summary>
        public int VoltageMv { get; set; }

        /// <summary>Stock boost frequency at this point in MHz - read-only, from the driver.</summary>
        public int BaseFrequencyMHz { get; set; }

        /// <summary>User-editable clock delta at this point, in MHz.</summary>
        public int OffsetMHz { get; set; }

        /// <summary>Driver-queried minimum delta for this point (MHz). Never assumed.</summary>
        public int MinOffsetMHz { get; set; }

        /// <summary>Driver-queried maximum delta for this point (MHz). Never assumed.</summary>
        public int MaxOffsetMHz { get; set; }

        /// <summary>Effective frequency after the offset is applied.</summary>
        public int EffectiveFrequencyMHz => BaseFrequencyMHz + OffsetMHz;
    }

    /// <summary>
    /// The GPU's base V/F curve (queried from NVAPI at session start) plus the
    /// user's current edited offsets. Base and edits are kept separate - the
    /// queried base is never mutated in place, so "reset to stock curve" is
    /// always trivially available.
    ///
    /// Only the graphics (core) domain participates: the V/F curve is a core
    /// boost table. Memory offsets stay on the existing memory-offset control.
    /// </summary>
    public sealed class GpuVoltageFrequencyCurve
    {
        public List<VfCurvePoint> Points { get; set; } = new();

        /// <summary>
        /// True when the driver answered the voltage-boost-percent query
        /// (GetCoreVoltageBoostPercent). False on Ampere/Ada where the vBIOS
        /// locks it - the UI must not render the control then, and must never
        /// imply true overvolting.
        /// </summary>
        public bool VoltageBoostSupported { get; set; }

        /// <summary>Current voltage-boost percent reported by the driver (0-100).</summary>
        public uint CurrentVoltageBoostPercent { get; set; }

        public int Count => Points.Count;

        /// <summary>Offsets only, in point order - the shape persisted on profiles.</summary>
        public IReadOnlyList<int> GetOffsets() => Points.Select(p => p.OffsetMHz).ToList();

        /// <summary>
        /// Applies UI edits onto a copy of the base: clamps every point to its
        /// driver-queried range. Returns a NEW list; the base stays untouched.
        /// </summary>
        public List<int> ClampOffsets(IReadOnlyList<int> edited)
        {
            var result = new List<int>(Points.Count);
            for (int i = 0; i < Points.Count; i++)
            {
                int want = i < edited.Count ? edited[i] : 0;
                result.Add(Math.Clamp(want, Points[i].MinOffsetMHz, Points[i].MaxOffsetMHz));
            }
            return result;
        }

        /// <summary>
        /// Simple-mode convenience: one flat offset across all points (clamped
        /// per-point). UI-only shorthand - the write path still commits the
        /// full per-point offset table, uniformly.
        /// </summary>
        public List<int> ExpandFlatOffset(int offsetMhz)
            => Points.Select(p => Math.Clamp(offsetMhz, p.MinOffsetMHz, p.MaxOffsetMHz)).ToList();

        /// <summary>
        /// Validates that the curve stays monotonically sensible: effective
        /// frequency must not decrease as voltage increases. Submitting a
        /// non-monotonic curve to the driver produces instability
        /// disproportionate to the actual clock delta, so this is checked
        /// client-side before any commit is allowed.
        /// </summary>
        public bool ValidateMonotonic(IReadOnlyList<int> edited, out string? error)
        {
            for (int i = 1; i < Points.Count; i++)
            {
                int prev = Points[i - 1].BaseFrequencyMHz + (i - 1 < edited.Count ? edited[i - 1] : 0);
                int cur = Points[i].BaseFrequencyMHz + (i < edited.Count ? edited[i] : 0);
                if (cur < prev)
                {
                    error = $"Point {i + 1} ({Points[i].VoltageMv} mV) would run below point {i} " +
                            $"({prev} MHz > {cur} MHz). Frequency must not decrease as voltage rises.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        /// <summary>Widest per-point editable span, for sizing simple-mode UI. Null when no points.</summary>
        public (int Min, int Max)? WidestOffsetRange()
        {
            if (Points.Count == 0) return null;
            return (Points.Min(p => p.MinOffsetMHz), Points.Max(p => p.MaxOffsetMHz));
        }
    }
}
