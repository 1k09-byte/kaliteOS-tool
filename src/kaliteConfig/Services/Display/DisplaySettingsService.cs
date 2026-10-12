// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Models;
using static kaliteConfig.Native.NativeMethods.Display;

namespace kaliteConfig.Services;

/// <summary>
/// Provides read/write access to display resolution, refresh rate, and rotation
/// via <c>EnumDisplaySettingsExW</c> and <c>ChangeDisplaySettingsExW</c>.
/// </summary>
internal static class DisplaySettingsService
{
    /// <summary>
    /// Gets all unique valid modes (resolution + refresh rate) for a display.
    /// Excludes interlaced modes and restricts to 32 bpp color.
    /// </summary>
    public static List<DisplayMode> GetAvailableModes(string deviceName)
    {
        var modes = new HashSet<DisplayMode>();
        var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };

        uint i = 0;
        while (EnumDisplaySettingsExW(deviceName, i, ref dm, 0))
        {
            if (dm.dmBitsPerPel == 32)
            {
                // If it claims interlaced, skip it; we only want progressive
                if ((dm.dmDisplayFlags & 2) == 0) // DM_INTERLACED == 2
                {
                    modes.Add(new DisplayMode
                    {
                        Width = dm.dmPelsWidth,
                        Height = dm.dmPelsHeight,
                        RefreshRate = dm.dmDisplayFrequency,
                        Orientation = (int)dm.dmDisplayOrientation,
                    });
                }
            }
            i++;
            dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        }

        // Return ordered descending: resolution first, then refresh rate
        return modes.OrderByDescending(m => m.Width)
                    .ThenByDescending(m => m.Height)
                    .ThenByDescending(m => m.RefreshRate)
                    .ToList();
    }

    /// <summary>Explains a <c>ChangeDisplaySettingsEx</c> return code in the user's terms.</summary>
    internal static string DescribeChangeResult(int code) => code switch
    {
        DISP_CHANGE_SUCCESSFUL => "applied",
        DISP_CHANGE_RESTART => "needs the system restarted",
        DISP_CHANGE_BADMODE => "the graphics driver does not offer that combination of " +
                               "resolution, refresh rate and rotation",
        DISP_CHANGE_NOTUPDATED => "the mode was recognised but could not be written to the registry",
        DISP_CHANGE_BADFLAGS => "Windows rejected the request flags",
        DISP_CHANGE_BADPARAM => "Windows rejected the mode as a malformed parameter - usually " +
                               "the refresh rate does not exist at the selected resolution, " +
                               "or this rotation is not supported on this display",
        DISP_CHANGE_FAILED => "the request failed",
        _ => $"failed (Windows returned {code})",
    };

    /// <summary>
    /// Applies a display mode (resolution, refresh rate) and optionally rotation.
    /// Modifies the registry permanently on success.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> on success; otherwise a sentence naming the reason the change
    /// was refused. This used to be a bare <see cref="bool"/>, which threw away the only
    /// information the caller had - the caller had nothing to tell the user, so a refused
    /// resolution looked identical to an unrelated failure.
    /// </returns>
    public static string? ApplyMode(string deviceName, DisplayMode targetMode, int? newRotation = null)
    {
        var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        if (!EnumDisplaySettingsExW(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            return "the display's current settings could not be read";

        bool isRotationChange = newRotation.HasValue && newRotation.Value != (int)dm.dmDisplayOrientation;
        
        dm.dmPelsWidth = targetMode.Width;
        dm.dmPelsHeight = targetMode.Height;
        dm.dmDisplayFrequency = targetMode.RefreshRate;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        if (newRotation.HasValue)
        {
            // If we are rotating from landscape to portrait (or vice versa),
            // Win32 requires us to swap the width and height we pass in!
            bool wasPortrait = dm.dmDisplayOrientation is DMDO_90 or DMDO_270;
            bool isPortrait = newRotation.Value is DMDO_90 or DMDO_270;

            if (wasPortrait != isPortrait)
            {
                dm.dmPelsWidth = targetMode.Height;
                dm.dmPelsHeight = targetMode.Width;
            }

            dm.dmDisplayOrientation = (uint)newRotation.Value;
            dm.dmFields |= DM_DISPLAYORIENTATION;
        }

        // Test the mode first
        int testRes = ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (testRes != DISP_CHANGE_SUCCESSFUL) return DescribeChangeResult(testRes);

        // Apply permanently
        int applyRes = ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return applyRes == DISP_CHANGE_SUCCESSFUL
            ? null
            : DescribeChangeResult(applyRes);
    }
}
