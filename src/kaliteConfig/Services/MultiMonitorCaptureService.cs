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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace kaliteConfig.Services;

/// <summary>
/// Captures all monitors simultaneously.
/// Currently uses GDI BitBlt for compatibility.
/// </summary>
public sealed class MultiMonitorCaptureService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;

    public MultiMonitorCaptureService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Captures all monitors simultaneously and returns the captured frames.
    /// </summary>
    /// <returns>List of captured frames with their monitor coordinates</returns>
    public async Task<List<CapturedMonitor>> CaptureAllMonitorsAsync()
    {
        var monitors = new List<CapturedMonitor>();
        var displayAreas = DisplayArea.FindAll();

        if (displayAreas.Count == 0)
            return monitors;

        // Capture all monitors in parallel using GDI (reusing existing SnipCaptureHelper)
        var captureTasks = displayAreas.Select(async area =>
        {
            try
            {
                var bounds = area.OuterBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return null;

                var pixels = await Task.Run(() => 
                    SnipCaptureHelper.CaptureScreenSlice(
                        (int)bounds.X, 
                        (int)bounds.Y, 
                        (int)bounds.Width, 
                        (int)bounds.Height));

                return new CapturedMonitor
                {
                    DisplayId = area.DisplayId.ToString(),
                    X = (int)bounds.X,
                    Y = (int)bounds.Y,
                    Width = (int)bounds.Width,
                    Height = (int)bounds.Height,
                    Pixels = pixels,
                    IsHdr = false
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to capture monitor {area.DisplayId}: {ex.Message}");
                return null;
            }
        }).ToList();

        var results = await Task.WhenAll(captureTasks);
        monitors.AddRange(results.Where(r => r != null));

        return monitors;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

/// <summary>
/// Represents a captured monitor frame.
/// </summary>
public class CapturedMonitor
{
    public string DisplayId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Pixels { get; set; } = Array.Empty<byte>();
    public bool IsHdr { get; set; }
}
