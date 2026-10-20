using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

public sealed class SnipService : IDisposable
{
    private readonly SnipHotkeyService _hotkey = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private kaliteConfig.Views.SnipOverlayWindow? _overlay;
    private List<kaliteConfig.Views.SnipOverlayWindow>? _overlays;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static string ForegroundAppName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName ?? "";
        }
        catch { return ""; }
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public bool HotkeyRegistered { get; private set; }

    public string HotkeyLabel { get; private set; } = SnipHotkeyService.Presets[0].Label;

    public string HotkeyError { get; private set; } = "";

    public string LastCaptureError { get; private set; } = "";

    public string HotkeyStatusText => HotkeyRegistered
        ? $"Hotkey: {HotkeyLabel}"
        : "Hotkey: not registered - click to fix";

    public event Action? HotkeyStatusChanged;

    public static event Action? GalleryChanged;
    public static void NotifyGalleryChanged()
    {
        try { GalleryChanged?.Invoke(); } catch { }
    }

    public static (int X, int Y, int W, int H)? LastRegion { get; set; }

    private static List<(int X, int Y, int W, int H)> GetAreas()
    {
        var areas = new List<(int X, int Y, int W, int H)>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var mi = new MONITORINFO();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    var rc = mi.rcMonitor;
                    int w = rc.Right - rc.Left;
                    int h = rc.Bottom - rc.Top;
                    if (w > 100 && h > 100)
                        areas.Add((rc.Left, rc.Top, w, h));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        
        if (areas.Count == 0)
            areas.Add((0, 0, 1920, 1080));
        areas.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return areas;
    }

    public async System.Threading.Tasks.Task<(bool Ok, string Message)> RepeatLastCaptureAsync()
    {
        var r = LastRegion;
        if (r is null || r.Value.W <= 0 || r.Value.H <= 0)
            return (false, "No previous region: drag a region first, then Repeat Last re-captures it.");
        try
        {
            var pixels = await System.Threading.Tasks.Task.Run(
                () => SnipCaptureHelper.CaptureScreenSlice(r.Value.X, r.Value.Y, r.Value.W, r.Value.H));
            var result = await SnipGalleryService.SaveSnipAsync(pixels, r.Value.W, r.Value.H, "kaliteConfig");
            bool copied = await CopyBgraAsync(pixels, r.Value.W, r.Value.H);
            NotifyGalleryChanged();
            return (true, $"Repeated {r.Value.W}×{r.Value.H} → {System.IO.Path.GetFileName(result.Path)}" +
                (copied ? " + clipboard." : " (clipboard copy failed).") +
                (result.IsUniform ? " Warning: flat color - protected content?" : ""));
        }
        catch (Exception ex)
        {
            return (false, "Repeat capture failed: " + ex.Message);
        }
    }

    public async System.Threading.Tasks.Task<(bool Ok, string Message)> CaptureFullscreenAsync()
    {
        try
        {
            if (_overlay != null)
            {
                try { _overlay.Close(); } catch { }
                _overlay = null;
            }
            var areas = GetAreas();

            if (areas.Count == 0)
                return (false, "No monitors detected.");

            int minX = areas.Min(a => a.X);
            int minY = areas.Min(a => a.Y);
            int maxX = areas.Max(a => a.X + a.W);
            int maxY = areas.Max(a => a.Y + a.H);
            int totalW = maxX - minX;
            int totalH = maxY - minY;

            byte[] stitchedPixels = new byte[totalW * totalH * 4];
            for (int i = 0; i < stitchedPixels.Length; i += 4)
            {
                stitchedPixels[i] = 0;
                stitchedPixels[i + 1] = 0;
                stitchedPixels[i + 2] = 0;
                stitchedPixels[i + 3] = 255;
            }

            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var (x, y, w, h) in areas)
                {
                    var monitorPixels = SnipCaptureHelper.CaptureScreenSlice(x, y, w, h);
                    int offsetX = x - minX;
                    int offsetY = y - minY;

                    for (int row = 0; row < h; row++)
                    {
                        int dstRow = row + offsetY;
                        if (dstRow < 0 || dstRow >= totalH) continue;

                        int srcOffset = row * w * 4;
                        int dstOffset = (dstRow * totalW + offsetX) * 4;

                        int bytesToCopy = Math.Min(w * 4, (totalW - offsetX) * 4);
                        if (dstOffset + bytesToCopy <= stitchedPixels.Length && dstOffset >= 0)
                        {
                            Array.Copy(monitorPixels, srcOffset, stitchedPixels, dstOffset, bytesToCopy);
                        }
                    }
                }
            });

            var result = await SnipGalleryService.SaveSnipAsync(stitchedPixels, totalW, totalH);
            bool copied = await CopyBgraAsync(stitchedPixels, totalW, totalH);
            NotifyGalleryChanged();
            return (true, $"Fullscreen {totalW}×{totalH} ({areas.Count} monitors) → {System.IO.Path.GetFileName(result.Path)}" +
                (copied ? " + clipboard." : " (clipboard copy failed).") +
                (result.IsUniform ? " Warning: flat color - protected content?" : ""));
        }
        catch (Exception ex)
        {
            return (false, "Fullscreen capture failed: " + ex.Message);
        }
    }

    private System.Threading.Tasks.Task<bool> CopyBgraAsync(byte[] bgra, int w, int h)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (_dispatcherQueue is null) { tcs.TrySetResult(false); return tcs.Task; }
            if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                    var bmp = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, bgra, w, h,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await bmp.SaveAsync(stream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    try
                    {
                        dp.SetData("DeviceIndependentBitmap", SnipRegionLogic.BuildDib32(bgra, w, h));
                    }
                    catch {  }
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    tcs.TrySetResult(true);
                }
                catch (Exception)
                {
                    tcs.TrySetResult(false);
                }
            }))
            {
                tcs.TrySetResult(false);
            }
        }
        catch { tcs.TrySetResult(false); }
        return tcs.Task;
    }

    public SnipService()
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _hotkey.Pressed += TriggerCapture;
        var settings = SnipSettingsService.Load();
        var (ok, message) = RebindHotkey(settings.HotkeyIndex);
        if (!ok)
        {
        }
    }

    public (bool Ok, string Message) TryCustomHotkey(uint modifiers, uint vk)
    {
        var preset = new SnipHotkeyService.HotkeyPreset(
            SnipHotkeyService.FormatLabel(modifiers, vk), modifiers, vk);
        var (ok, message) = _hotkey.Register(preset);
        if (!ok) return (false, message);
        HotkeyRegistered = true;
        HotkeyLabel = preset.Label;
        HotkeyError = "";
        HotkeyStatusChanged?.Invoke();
        return (true, $"{preset.Label} is active.");
    }

    public (bool Ok, string Message) RebindHotkey(int index)
    {
        SnipHotkeyService.HotkeyPreset preferred;
        if (index == SnipHotkeyService.CustomIndex)
        {
            var cs = SnipSettingsService.Load();
            preferred = new SnipHotkeyService.HotkeyPreset(
                SnipHotkeyService.FormatLabel(cs.CustomHotkeyModifiers, cs.CustomHotkeyVk),
                cs.CustomHotkeyModifiers, cs.CustomHotkeyVk);
        }
        else
        {
            if (index < 0 || index >= SnipHotkeyService.Presets.Length) index = 0;
            preferred = SnipHotkeyService.Presets[index];
        }

        var (ok, message) = _hotkey.Register(preferred);
        if (ok)
        {
            HotkeyRegistered = true;
            HotkeyLabel = preferred.Label;
            HotkeyError = "";
            HotkeyStatusChanged?.Invoke();
            return (true, $"{preferred.Label} is active.");
        }

        var errors = new List<string> { $"{preferred.Label}: {message}" };
        foreach (var preset in SnipHotkeyService.Presets)
        {
            if (string.Equals(preset.Label, preferred.Label, StringComparison.Ordinal)) continue;
            var (altOk, altMessage) = _hotkey.Register(preset);
            if (altOk)
            {
                HotkeyRegistered = true;
                HotkeyLabel = preset.Label;
                HotkeyError = string.Join(" | ", errors);
                HotkeyStatusChanged?.Invoke();
                return (true,
                    $"{preferred.Label} is taken, so capture now uses {preset.Label}. {message}");
            }
            errors.Add($"{preset.Label}: {altMessage}");
        }

        HotkeyRegistered = false;
        HotkeyError = string.Join(" | ", errors);
        HotkeyStatusChanged?.Invoke();
        return (false, HotkeyError);
    }

    private static void Log(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "snip-capture.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    public void CloseOverlays()
    {
        if (_overlay != null)
        {
            try { _overlay.Close(); } catch { }
            _overlay = null;
        }
        if (_overlays != null)
        {
            foreach (var ov in _overlays.ToArray())
            {
                try { ov.Close(); } catch { }
            }
            _overlays = null;
        }
    }

    public void DismissOtherMonitors(kaliteConfig.Views.SnipOverlayWindow activeOverlay)
    {
        if (_overlays != null)
        {
            var others = _overlays.Where(o => o != activeOverlay).ToArray();
            foreach (var ov in others)
            {
                try { ov.Close(); } catch { }
                _overlays.Remove(ov);
            }
        }
    }

    public void TriggerCapture()
    {
        CloseOverlays();
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            BeginCapture();
        });
    }

    public static List<(int X, int Y, int W, int H)> MonitorAreas { get; private set; } = new();
    public static int CursorMonitorIndex { get; private set; }

    public void ShowPickerOnCursorMonitor()
    {
        var ov = _overlays;
        if (ov is null || ov.Count == 0) return;
        int idx = (CursorMonitorIndex >= 0 && CursorMonitorIndex < ov.Count) ? CursorMonitorIndex : 0;
        try
        {
            var target = ov[idx];
            target.DispatcherQueue.TryEnqueue(() =>
            {
                try { target.Activate(); target.ShowMonitorPicker(); }
                catch (Exception ex) { Log("cursor-picker ERR " + ex.Message); }
            });
        }
        catch (Exception ex) { Log("cursor-picker ERR " + ex.Message); }
    }

    public void SelectFullscreenOnCursorMonitor()
    {
        var ov = _overlays;
        if (ov is null || ov.Count == 0) return;
        int idx = (CursorMonitorIndex >= 0 && CursorMonitorIndex < ov.Count) ? CursorMonitorIndex : 0;
        try
        {
            var target = ov[idx];
            target.DispatcherQueue.TryEnqueue(() =>
            {
                try { target.Activate(); target.SelectFullscreen(); }
                catch (Exception ex) { Log("cursor-fullscreen ERR " + ex.Message); }
            });
        }
        catch (Exception ex) { Log("cursor-fullscreen ERR " + ex.Message); }
    }
    public void CaptureMonitorFullscreen(int index)
    {
        var ov = _overlays;
        if (ov is null || index < 0 || index >= ov.Count) return;
        try { ov[index].FullscreenCopy(); }
        catch (Exception ex) { Log("monitor-fullscreen ERR " + ex.Message); }
    }

    private void BeginCapture()
    {

        string foregroundApp = ForegroundAppName();
        if (!string.IsNullOrEmpty(foregroundApp)) Log($"Foreground app at capture: {foregroundApp}");

        var areas = GetAreas();

        Log($"Detected {areas.Count} monitors:");
        for (int i = 0; i < areas.Count; i++)
        {
            Log($"  Monitor {i}: pos=({areas[i].X},{areas[i].Y}) size={areas[i].W}x{areas[i].H}");
        }

        int cursorMonitorIndex = 0;
        if (GetCursorPos(out POINT cursor))
        {
            for (int i = 0; i < areas.Count; i++)
            {
                var (mx, my, mw, mh) = areas[i];
                if (cursor.X >= mx && cursor.X < mx + mw && cursor.Y >= my && cursor.Y < my + mh)
                {
                    cursorMonitorIndex = i;
                    break;
                }
            }
            Log($"Cursor at ({cursor.X},{cursor.Y}) on monitor {cursorMonitorIndex}");
        }
        MonitorAreas = areas;
        CursorMonitorIndex = cursorMonitorIndex;

        var captureTasks = areas.Select(area =>
            System.Threading.Tasks.Task.Run(() => SnipCaptureHelper.CaptureScreenSlice(area.X, area.Y, area.W, area.H))
        ).ToList();

        _ = CreateOverlaysAsync(areas, captureTasks, foregroundApp, cursorMonitorIndex);
    }

    private async System.Threading.Tasks.Task CreateOverlaysAsync(
        List<(int X, int Y, int W, int H)> areas,
        List<System.Threading.Tasks.Task<byte[]>> captureTasks,
        string foregroundApp,
        int cursorMonitorIndex)
    {
        try
        {
            Log($"Starting capture for {areas.Count} monitors");
            var pixelArrays = await System.Threading.Tasks.Task.WhenAll(captureTasks).ConfigureAwait(false);
            Log($"Capture complete, creating {pixelArrays.Length} overlays");

            if (_dispatcherQueue is null)
            {
                LastCaptureError = "Cannot open the snip overlay: the UI dispatcher queue is not available.";
                Log("ERROR no dispatcher queue; cannot create overlays");
                return;
            }

            var queued = _dispatcherQueue.TryEnqueue(() =>
            {
                _overlays = new List<kaliteConfig.Views.SnipOverlayWindow>();
                for (int i = 0; i < areas.Count; i++)
                {
                        var (x, y, w, h) = areas[i];
                        var pixels = pixelArrays[i];
                        Log($"Creating overlay {i}: pos=({x},{y}) size={w}x{h} pixels={pixels.Length}");
                        
                        var overlay = new kaliteConfig.Views.SnipOverlayWindow();
                        overlay.LoadBitmap(pixels, w, h);
                        overlay.MonitorIndex = i;
                        overlay.SourceApp = foregroundApp;
                        overlay.PlaceOnMonitor(x, y, w, h);
                        overlay.Closed += (_, _) =>
                        {
                            _overlays?.Remove(overlay);
                            Log($"Overlay {i} closed, remaining: {_overlays?.Count ?? 0}");
                            if (_overlays?.Count == 0)
                            {
                                _overlays = null;
                                _overlay = null;
                            }
                        };
                        _overlays.Add(overlay);

                        if (i == cursorMonitorIndex)
                        {
                            _overlay = overlay;
                            Log($"Overlay {i} is cursor monitor");
                        }
                        overlay.Activate();
                        Log($"Overlay {i} activated");
                    }
                    LastCaptureError = "";
                    Log($"All {_overlays.Count} overlays created");
                });

                if (!queued)
                {
                    LastCaptureError = "Cannot open the snip overlay: the UI thread did not accept the request.";
                    Log("ERROR TryEnqueue rejected overlay creation");
                }
            }
            catch (Exception ex)
            {
                LastCaptureError = $"Multi-monitor capture failed: {ex.Message}";
                Log($"Capture error: {ex}");
                _dispatcherQueue?.TryEnqueue(() => CaptureFailed?.Invoke(LastCaptureError));
            }
    }

    public event Action<string>? CaptureFailed;

    public async System.Threading.Tasks.Task OpenInEditorAsync(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
    {
        var overlay = new kaliteConfig.Views.SnipOverlayWindow();
        
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var provider = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            
        var pixels = provider.DetachPixelData();
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;

        _dispatcherQueue?.TryEnqueue(() =>
        {
            overlay.LoadBitmap(pixels, w, h);
            _overlay = overlay;
            overlay.Activate();
        });
    }

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
        catch (Exception)
        {
        }
    }

    public void OpenInViewer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;
        _dispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var viewer = new kaliteConfig.Views.SnipViewerWindow(filePath);
                viewer.Activate();
            }
            catch (Exception)
            {
            }
        });
    }

    public void OpenBitmapInEditor(byte[] pixels, int w, int h)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var overlay = new kaliteConfig.Views.SnipOverlayWindow();
            overlay.LoadBitmap(pixels, w, h);
            _overlay = overlay;
            overlay.Activate();
        });
    }

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
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        try { _overlay?.Close(); } catch { }
        _overlay = null;
        _hotkey.Dispose();
    }
}
