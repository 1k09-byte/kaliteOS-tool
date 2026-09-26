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

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// "Nice" axis ticks (1/2/2.5/5 × 10^n) for the V/F curve graph. Pure
    /// math, no UI - unit-tested in the OcVerify harness, used by the
    /// OverclockSection canvas.
    /// </summary>
    public static class GraphTicks
    {
        /// <summary>Tick step and first tick ≥ min for at most maxTicks ticks.</summary>
        public static (double Step, double First) NiceTicks(double min, double max, int maxTicks)
        {
            double span = Math.Max(1e-9, max - min);
            double raw = span / Math.Max(1, maxTicks);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * mag;
            return (step, Math.Ceiling(min / step) * step);
        }

        /// <summary>Compact tick label: integers plain, fractional with one decimal.</summary>
        public static string Label(double v) => v % 1 == 0 ? ((int)v).ToString() : v.ToString("0.#");
    }
}
