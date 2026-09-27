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
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// The ADLX value translations, with no interop in sight.
    ///
    /// Every one of these exists because ADLX and the tuning UI disagree about
    /// what a number means, and the disagreements are documented in the ADLX
    /// headers rather than invented:
    ///
    ///   - max VRAM frequency is ABSOLUTE MHz on ADLX, while the UI's memory
    ///     control is an OFFSET from the value found at detection;
    ///   - max core frequency is an offset from Navi4+ and an absolute
    ///     frequency before that ("Start from Navi4+, the maximum frequency is an
    ///     offset from the base frequency" - IGPUManualGFXTuning.h), and ADLX
    ///     exposes no ASIC generation to tell the two apart;
    ///   - power limit is already a percent on both sides.
    ///
    /// So: pure functions, no pointers, fully unit tested. A wrong translation
    /// here does not fail loudly - it quietly runs a GPU at the wrong clock -
    /// which is exactly the kind of bug that must not live inside an
    /// interop method where a test cannot reach it.
    /// </summary>
    internal static class AmdTuningMapping
    {
        /// <summary>
        /// Whether a driver-reported core-frequency range can be read as an
        /// offset range.
        ///
        /// An offset range straddles zero (Navi4+ reports something like
        /// -500..+500). An absolute-frequency range is a block of positive
        /// clocks (roughly 200..3500). ADLX gives no generation flag, so the
        /// sign of the minimum is the only evidence available - and it is
        /// evidence, not proof, which is why a range we cannot read is reported
        /// as unsupported rather than guessed at. Guessing would be the
        /// expensive direction: tell a driver that wants an absolute frequency
        /// that you mean an offset and the card ends up far slower than it
        /// should, and the user has no way to tell why.
        /// </summary>
        internal static bool ReportsOffsetSemantics(AdlxIntRange range) => range.MinValue <= 0;

        /// <summary>Offset range shown to the user, for a driver offset range.</summary>
        internal static OverclockControlRange ToControlRange(AdlxIntRange range) =>
            new(range.MinValue, range.MaxValue, Math.Max(1, range.Step));

        /// <summary>
        /// The offset range the UI should show for an API that reports an
        /// ABSOLUTE range. Shifting by the baseline is not cosmetic: hand the
        /// UI 20000..24000 and the slider says "20 GHz", the setter treats the
        /// value as an offset from 20 GHz, and the card is asked for 40 GHz.
        /// Both halves have to speak offsets, so both are shifted here.
        /// </summary>
        internal static OverclockControlRange ToOffsetControlRange(AdlxIntRange absolute, int baseline) =>
            new(absolute.MinValue - baseline, absolute.MaxValue - baseline, Math.Max(1, absolute.Step));

        /// <summary>
        /// Converts a requested offset into the absolute value to hand ADLX, for
        /// an API that speaks absolute MHz (VRAM always; core before Navi4).
        /// </summary>
        internal static int ToAbsolute(int baseline, int offset) => baseline + offset;

        /// <summary>The inverse: what offset the card is currently sitting at.</summary>
        internal static int ToOffset(int baseline, int absolute) => absolute - baseline;

        /// <summary>
        /// Clamps a value into a driver-reported range and snaps it to the
        /// driver's step. The step is honoured relative to the range minimum,
        /// which is what the driver itself does when it validates a write.
        /// </summary>
        internal static int ClampToRange(AdlxIntRange range, int value)
        {
            int step = range.Step > 0 ? range.Step : 1;
            int snapped = range.MinValue + ((value - range.MinValue) / step) * step;
            return Math.Clamp(snapped, range.MinValue, range.MaxValue);
        }

        /// <summary>
        /// A rejected write, in one place: outside the driver's own range means
        /// the value never reaches the hardware.
        /// </summary>
        internal static bool WithinRange(AdlxIntRange range, int value) =>
            value >= range.MinValue && value <= range.MaxValue;
    }
}
