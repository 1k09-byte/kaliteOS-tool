using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.GpuOverclock.Models
{
    public sealed class VfCurvePoint
    {
        public int VoltageMv { get; set; }

        public int BaseFrequencyMHz { get; set; }

        public int OffsetMHz { get; set; }

        public int MinOffsetMHz { get; set; }

        public int MaxOffsetMHz { get; set; }

        public int EffectiveFrequencyMHz => BaseFrequencyMHz + OffsetMHz;
    }

    public sealed class GpuVoltageFrequencyCurve
    {
        public List<VfCurvePoint> Points { get; set; } = new();

        public bool VoltageBoostSupported { get; set; }

        public uint CurrentVoltageBoostPercent { get; set; }

        public int Count => Points.Count;

        public IReadOnlyList<int> GetOffsets() => Points.Select(p => p.OffsetMHz).ToList();

        public List<int> ClampOffsets(IReadOnlyList<int> edited)
        {
            var result = new List<int>(Points.Count);
            for (int i = 0; i < Points.Count; i++)
            {
                int lo = Math.Min(Points[i].MinOffsetMHz, Points[i].MaxOffsetMHz);
                int hi = Math.Max(Points[i].MinOffsetMHz, Points[i].MaxOffsetMHz);
                int want = i < edited.Count ? edited[i] : 0;
                result.Add(Math.Clamp(want, lo, hi));
            }
            return result;
        }

        public List<int> ExpandFlatOffset(int offsetMhz)
            => Points.Select(p =>
            {
                int lo = Math.Min(p.MinOffsetMHz, p.MaxOffsetMHz);
                int hi = Math.Max(p.MinOffsetMHz, p.MaxOffsetMHz);
                return Math.Clamp(offsetMhz, lo, hi);
            }).ToList();

        public bool ValidateMonotonic(IReadOnlyList<int> edited, out string? error)
        {
            for (int i = 1; i < Points.Count; i++)
            {
                int stockPrev = Points[i - 1].BaseFrequencyMHz + Points[i - 1].OffsetMHz;
                int stockCur = Points[i].BaseFrequencyMHz + Points[i].OffsetMHz;
                if (stockCur + 1 < stockPrev) continue;

                int prev = Points[i - 1].BaseFrequencyMHz + (i - 1 < edited.Count ? edited[i - 1] : 0);
                int cur = Points[i].BaseFrequencyMHz + (i < edited.Count ? edited[i] : 0);
                if (cur + 1 < prev)
                {
                    error = $"Point {i + 1} ({Points[i].VoltageMv} mV) would run below point {i} " +
                            $"({prev} MHz > {cur} MHz). Frequency must not decrease as voltage rises.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        public (int Min, int Max)? WidestOffsetRange()
        {
            if (Points.Count == 0) return null;
            return (Points.Min(p => p.MinOffsetMHz), Points.Max(p => p.MaxOffsetMHz));
        }
    }
}
