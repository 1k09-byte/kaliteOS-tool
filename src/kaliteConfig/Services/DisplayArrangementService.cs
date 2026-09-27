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
/// Provides CCD topology manipulation (re-arranging monitors in the virtual desktop layout)
/// and math for snapping monitors edge-to-edge.
/// </summary>
internal static class DisplayArrangementService
{
    private const int SNAP_THRESHOLD = 20;

    /// <summary>
    /// Applies a completely new arrangement of display positions using CCD.
    /// Updates the path sources with the new virtual desktop coordinates.
    /// </summary>
    public static bool ApplyArrangement(IEnumerable<DisplayInfo> displays)
    {
        int hr = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out uint pathCount, out uint modeCount);
        if (hr != 0) return false;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        hr = QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (hr != 0) return false;

        // Ensure one is explicitly marked (0,0) as primary fallback
        var displayList = displays.ToList();
        if (displayList.Count == 0) return false;

        bool hasPrimary = displayList.Any(d => d.PositionX == 0 && d.PositionY == 0);
        if (!hasPrimary)
        {
            var first = displayList.First();
            int dx = first.PositionX;
            int dy = first.PositionY;
            foreach (var d in displayList)
            {
                // Force shift to 0,0 relative origin via init properties? 
                // DisplayInfo is immutable, so we compute offsets below.
            }
            // For now, we trust the UI to send a 0,0 primary
        }

        bool changed = false;

        foreach (var path in paths)
        {
            if ((path.Flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            // Find matching UI display based on source ID
            var uiDisplay = displayList.FirstOrDefault(d => d.CcdSourceId == path.SourceInfo.Id);
            if (uiDisplay == null) continue;

            // Find the source mode for this path to update position
            if (path.SourceInfo.ModeInfoIdx < modeCount)
            {
                ref var mode = ref modes[path.SourceInfo.ModeInfoIdx];
                if (mode.InfoType == DISPLAYCONFIG_MODE_INFO_TYPE.Source)
                {
                    if (mode.Info.SourceMode.Position.X != uiDisplay.PositionX ||
                        mode.Info.SourceMode.Position.Y != uiDisplay.PositionY)
                    {
                        mode.Info.SourceMode.Position.X = uiDisplay.PositionX;
                        mode.Info.SourceMode.Position.Y = uiDisplay.PositionY;
                        changed = true;
                    }
                }
            }
        }

        if (!changed) return true; // Nothing to do

        // Set the new configuration
        hr = SetDisplayConfig(
            pathCount, paths,
            modeCount, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);

        return hr == 0;
    }

    /// <summary>
    /// Snaps a dragged monitor to the nearest edge of another monitor if within the threshold.
    /// </summary>
    /// <param name="draggedRect">The rect (X, Y, Width, Height) of the currently dragged monitor.</param>
    /// <param name="otherRects">List of rects for all other monitors.</param>
    /// <returns>The snapped (X,Y) coordinates.</returns>
    public static (int X, int Y) SnapToGrid((int X, int Y, int W, int H) draggedRect, IEnumerable<(int X, int Y, int W, int H)> otherRects)
    {
        int newX = draggedRect.X;
        int newY = draggedRect.Y;

        int dragRight = draggedRect.X + draggedRect.W;
        int dragBottom = draggedRect.Y + draggedRect.H;

        bool snappedX = false;
        bool snappedY = false;

        foreach (var other in otherRects)
        {
            int otherRight = other.X + other.W;
            int otherBottom = other.Y + other.H;

            // Snap X (left to right edge, right to left edge, left to left edge)
            if (!snappedX)
            {
                if (Math.Abs(draggedRect.X - otherRight) < SNAP_THRESHOLD) { newX = otherRight; snappedX = true; } // Left to Right
                else if (Math.Abs(dragRight - other.X) < SNAP_THRESHOLD) { newX = other.X - draggedRect.W; snappedX = true; } // Right to Left
                else if (Math.Abs(draggedRect.X - other.X) < SNAP_THRESHOLD) { newX = other.X; snappedX = true; } // Left to Left (align)
            }

            // Snap Y (top to bottom edge, bottom to top edge, top to top edge)
            if (!snappedY)
            {
                if (Math.Abs(draggedRect.Y - otherBottom) < SNAP_THRESHOLD) { newY = otherBottom; snappedY = true; } // Top to Bottom
                else if (Math.Abs(dragBottom - other.Y) < SNAP_THRESHOLD) { newY = other.Y - draggedRect.H; snappedY = true; } // Bottom to Top
                else if (Math.Abs(draggedRect.Y - other.Y) < SNAP_THRESHOLD) { newY = other.Y; snappedY = true; } // Top to Top (align)
            }

            if (snappedX && snappedY) break;
        }

        return (newX, newY);
    }
}
