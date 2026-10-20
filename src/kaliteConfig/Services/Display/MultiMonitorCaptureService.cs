using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace kaliteConfig.Services;

public sealed class MultiMonitorCaptureService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;

    public MultiMonitorCaptureService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task<List<CapturedMonitor>> CaptureAllMonitorsAsync()
    {
        var monitors = new List<CapturedMonitor>();
        var displayAreas = DisplayArea.FindAll();

        if (displayAreas.Count == 0)
            return monitors;

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
                    DisplayId = area.DisplayId.Value.ToString(),
                    X = (int)bounds.X,
                    Y = (int)bounds.Y,
                    Width = (int)bounds.Width,
                    Height = (int)bounds.Height,
                    Pixels = pixels,
                    IsHdr = false
                };
            }
            catch (Exception)
            {
                return null;
            }
        }).ToList();

        var results = await Task.WhenAll(captureTasks);
        monitors.AddRange(results.Where(r => r != null)!);

        return monitors;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

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
