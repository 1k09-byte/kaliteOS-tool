// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================

namespace kaliteConfig.Services;

/// <summary>
/// The one lock every NVAPI caller in this process shares.
///
/// NVAPI is explicitly not re-entrant across threads. The overclocking controller polls
/// telemetry on a background loop while the display panel may be running in the same hub,
/// so a per-class lock is not enough — two classes with two locks still race. Every entry
/// point that talks to the driver takes <see cref="Gate"/>.
/// </summary>
internal static class NvApiSession
{
    internal static readonly object Gate = new();
}
