// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Runtime.InteropServices;
using static kaliteConfig.Native.NativeMethods.Display;

namespace kaliteConfig.Services;

/// <summary>
/// Probes and toggles Windows Advanced Color (HDR) via DisplayConfigGetDeviceInfo.
/// </summary>
internal static class DisplayHdrService
{
    public enum HdrState
    {
        Enabled,
        Disabled,
        NotSupported,
        ForceDisabled
    }

    /// <summary>Queries the Advanced Color support and current state for a display target.</summary>
    public static HdrState GetHdrState(Native.NativeMethods.Display.LUID adapterId, uint targetId)
    {
        var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        colorInfo.Header.Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GetAdvancedColorInfo;
        colorInfo.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
        colorInfo.Header.AdapterId = adapterId;
        colorInfo.Header.Id = targetId;

        int hr = DisplayConfigGetDeviceInfo(ref colorInfo);
        if (hr != 0)
        {
            return HdrState.NotSupported;
        }

        HdrState state;
        if (!colorInfo.AdvancedColorSupported) state = HdrState.NotSupported;
        else if (colorInfo.AdvancedColorForceDisabled) state = HdrState.ForceDisabled;
        else state = colorInfo.AdvancedColorEnabled ? HdrState.Enabled : HdrState.Disabled;

        // The raw bits are logged alongside the derived state because "HDR says it is on"
        // and "HDR is actually on" are different questions, and only the bits settle which
        // one the panel is answering.
        return state;
    }

    /// <summary>Toggles Advanced Color for a display target.</summary>
    public static bool SetHdrEnabled(Native.NativeMethods.Display.LUID adapterId, uint targetId, bool enable)
    {
        // First check if it's supported
        var state = GetHdrState(adapterId, targetId);
        if (state == HdrState.NotSupported || state == HdrState.ForceDisabled)
        {
            return false;
        }

        var colorState = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
        colorState.Header.Type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SetAdvancedColorState;
        colorState.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
        colorState.Header.AdapterId = adapterId;
        colorState.Header.Id = targetId;
        colorState.Value = enable ? 1u : 0u;

        int hr = DisplayConfigSetDeviceInfo(ref colorState);

        // Re-read instead of trusting the status code: Windows can accept the call and
        // leave Advanced Color exactly where it was.
        var after = GetHdrState(adapterId, targetId);
        bool took = after == (enable ? HdrState.Enabled : HdrState.Disabled);
        return took;
    }
}
