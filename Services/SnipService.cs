using System;
using System.Collections.Generic;

namespace kaliteConfig.Services;

public sealed class SnipService : IDisposable
{
    private readonly SnipHotkeyService _hotkey = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private kaliteConfig.Views.SnipOverlayWindow? _overlay;
    private int _monitorIndex;

    public SnipService()
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _hotkey.Pressed += TriggerCapture;
        // Auto-register
        var settings = SnipSettingsService.Load();
        RebindHotkey(settings.HotkeyIndex);
    }
    
    public (bool Ok, string Message) RebindHotkey(int index)
    {
        if (index >= 0 && index < SnipHotkeyService.Presets.Length)
        {
            return _hotkey.Register(SnipHotkeyService.Presets[index]);
        }
        return (false, "Invalid index");
    }

    public void TriggerCapture()
    {
        // One overlay per monitor: pressing the hotkey while the overlay is open
        // closes it and jumps to the next monitor (wraps around).
        var areas = new List<(int X, int Y, int W, int H)>();
        try
        {
            foreach (var a in Microsoft.UI.Windowing.DisplayArea.FindAll())
            {
                var b = a.OuterBounds;
                if (b.Width > 0 && b.Height > 0)
                    areas.Add((b.X, b.Y, (int)b.Width, (int)b.Height));
            }
        }
        catch { }
        if (areas.Count == 0)
            areas.Add((0, 0, 1920, 1080));
        areas.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        if (_overlay != null)
        {
            try { _overlay.Close(); } catch { }
            _overlay = null;
            _monitorIndex++;
        }
        _monitorIndex %= areas.Count;
        var (x, y, w, h) = areas[_monitorIndex];

        byte[] pixels = SnipCaptureHelper.CaptureScreenSlice(x, y, w, h);

        // Ensure UI thread execution
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var overlay = new kaliteConfig.Views.SnipOverlayWindow();
            overlay.LoadBitmap(pixels, w, h);
            if (areas.Count > 1) overlay.PlaceOnMonitor(x, y);
            overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };
            _overlay = overlay;
            overlay.Activate();
        });
    }

    /// <summary>Opens an existing image file in the snip overlay editor.</summary>
    public void OpenInEditor(string filePath)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(filePath);
            using var ms = new System.IO.MemoryStream(bytes);
            var ras = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms);
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            var provider = decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var pixels = provider.DetachPixelData();
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            _dispatcherQueue?.TryEnqueue(() =>
            {
                var overlay = new kaliteConfig.Views.SnipOverlayWindow();
                overlay.LoadBitmap(pixels, w, h);
                _overlay = overlay;
                overlay.Activate();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("OpenInEditor failed: " + ex.Message);
        }
    }

    /// <summary>Pins an existing image file to the screen.</summary>
    public void PinImageFile(string filePath)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(filePath);
            using var ms = new System.IO.MemoryStream(bytes);
            var ras = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms);
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            var provider = decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var pixels = provider.DetachPixelData();
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            _dispatcherQueue?.TryEnqueue(() =>
            {
                var pin = new kaliteConfig.Views.SnipPinWindow(pixels, w, h);
                pin.Activate();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PinImageFile failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _overlay?.Close(); } catch { }
        _overlay = null;
        _hotkey.Dispose();
    }
}
