// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Runtime.InteropServices;
using static kaliteConfig.Native.NativeMethods.Display;

namespace kaliteConfig.Services;

/// <summary>
/// A safety countdown timer to auto-revert display (resolution/rotation/topology)
/// changes if the user doesn't confirm them.
/// Modeled heavily on <see cref="GpuOverclock.Services.SafetyRevertService"/>.
/// </summary>
internal static class DisplayRevertService
{
    public static event Action<int>? CountdownTicked;
    public static event Action? StateChanged;

    private static System.Threading.Timer? _timer;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private static int _secondsRemaining;
    private static bool _awaitingConfirmation;

    private static DISPLAYCONFIG_PATH_INFO[]? _anchorPaths;
    private static DISPLAYCONFIG_MODE_INFO[]? _anchorModes;

    public static bool IsAwaitingConfirmation => _awaitingConfirmation;
    public static int SecondsRemaining => _secondsRemaining;

    /// <summary>Captures the current CCD topology as the known-good anchor.</summary>
    public static void CaptureAnchor()
    {
        int hr = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out uint pathCount, out uint modeCount);
        if (hr != 0) return;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        hr = QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (hr == 0)
        {
            _anchorPaths = paths;
            _anchorModes = modes;
        }
    }

    /// <summary>Starts a 15-second countdown to auto-revert unless confirmed.</summary>
    public static void StartCountdown(int seconds = 15)
    {
        // The countdown runs on a System.Threading.Timer, so the tick lands on a pool
        // thread. Capturing the queue here — this method is always called from the UI
        // thread — is what lets the tick come back before it touches the display or the
        // bound state, instead of reverting the desktop underneath an in-flight edit.
        _dispatcherQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _awaitingConfirmation = true;
        _secondsRemaining = seconds;
        StateChanged?.Invoke();

        _timer?.Dispose();
        _timer = new System.Threading.Timer(OnTick, null, 1000, 1000);
    }

    /// <summary>User explicitly confirmed the new settings. Discards the anchor.</summary>
    public static void Confirm()
    {
        EndCountdown();
        // Discard anchor - the new state is now considered known-good.
        _anchorPaths = null;
        _anchorModes = null;
    }

    /// <summary>Reverts to the saved anchor topology immediately.</summary>
    public static void RevertNow()
    {
        EndCountdown();
        RestoreAnchor();
    }

    private static void EndCountdown()
    {
        _awaitingConfirmation = false;
        _timer?.Dispose();
        _timer = null;
        StateChanged?.Invoke();
    }

    private static void OnTick(object? state)
    {
        var queue = _dispatcherQueue;
        if (queue is null) return;

        queue.TryEnqueue(() =>
        {
            // A tick that was already queued when the countdown ended must not revive it.
            if (_timer is null) return;

            if (_secondsRemaining <= 1)
            {
                RevertNow(); // Expired!
                return;
            }

            _secondsRemaining--;
            CountdownTicked?.Invoke(_secondsRemaining);
        });
    }

    private static void RestoreAnchor()
    {
        if (_anchorPaths == null || _anchorModes == null) return;

        // Restore using CCD
        _ = SetDisplayConfig(
            (uint)_anchorPaths.Length, _anchorPaths,
            (uint)_anchorModes.Length, _anchorModes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);

        _anchorPaths = null;
        _anchorModes = null;
    }
}
