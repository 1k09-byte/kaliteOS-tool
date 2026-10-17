using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services.Personalize.Native;

// IDesktopWallpaper (shobjidl_core.h). Method set from the Microsoft docs page
// "IDesktopWallpaper interface"; vtable order cross-checked against a published
// C# binding. Method order MUST NOT change. Created on the calling thread, which
// is the UI STA thread for this app.
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
    void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
    void GetMonitorDevicePathCount(out uint count);
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out DesktopMonitorRect rect);
    void SetBackgroundColor(uint color);
    void GetBackgroundColor(out uint color);
    void SetPosition(int position);
    void GetPosition(out int position);
    void SetSlideshow(IntPtr items);
    void GetSlideshow(out IntPtr items);
    void SetSlideshowOptions(int options, uint slideshowTick);
    [PreserveSig]
    int GetSlideshowOptions(out int options, out uint slideshowTick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorId, int direction);
    void GetStatus(out int state);
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DesktopMonitorRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

// Values of DESKTOP_WALLPAPER_POSITION from the docs page of the same name.
internal static class DesktopWallpaperPosition
{
    public const int Center = 0;
    public const int Tile = 1;
    public const int Stretch = 2;
    public const int Fit = 3;
    public const int Fill = 4;
    public const int Span = 5;
}

// Wraps the COM object. Dispose releases the RCW; the object is not kept.
internal sealed class DesktopWallpaperApi : IDisposable
{
    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
    private readonly IDesktopWallpaper _wp;

    public DesktopWallpaperApi()
    {
        var type = Type.GetTypeFromCLSID(ClsidDesktopWallpaper, throwOnError: true)!;
        _wp = (IDesktopWallpaper)Activator.CreateInstance(type)!;
    }

    public IReadOnlyList<(string Id, DesktopMonitorRect Rect)> Monitors()
    {
        _wp.GetMonitorDevicePathCount(out uint count);
        var list = new List<(string, DesktopMonitorRect)>();
        for (uint i = 0; i < count; i++)
        {
            _wp.GetMonitorDevicePathAt(i, out string id);
            _wp.GetMonitorRECT(id, out var rect);
            list.Add((id, rect));
        }
        return list;
    }

    public string? GetWallpaper(string monitorId)
    {
        _wp.GetWallpaper(monitorId, out string path);
        return path;
    }

    public void SetWallpaper(string monitorId, string path) => _wp.SetWallpaper(monitorId, path);

    public int GetPosition()
    {
        _wp.GetPosition(out int pos);
        return pos;
    }

    public void SetPosition(int position) => _wp.SetPosition(position);

    public uint GetBackgroundColor()
    {
        _wp.GetBackgroundColor(out uint color);
        return color;
    }

    public void SetBackgroundColor(uint color) => _wp.SetBackgroundColor(color);

    public void SetSlideshowOptions(bool shuffle, uint intervalMs) =>
        _wp.SetSlideshowOptions(shuffle ? 1 : 0, intervalMs);

    public void AdvanceSlideshow(string monitorId) => _wp.AdvanceSlideshow(monitorId, 0);

    // Points the slideshow at one folder. The PIDL is freed with CoTaskMemFree and
    // the item array is released after SetSlideshow, as the shell docs require.
    public void SetSlideshowFolder(string folderPath)
    {
        int hr = NativeMethods.SHParseDisplayName(folderPath, IntPtr.Zero, out IntPtr pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) throw new InvalidOperationException($"Cannot open folder (0x{hr:X8}).");
        IntPtr array = IntPtr.Zero;
        try
        {
            hr = NativeMethods.SHCreateShellItemArrayFromIDLists(1, new[] { pidl }, out array);
            if (hr != 0 || array == IntPtr.Zero) throw new InvalidOperationException($"Cannot create slideshow list (0x{hr:X8}).");
            _wp.SetSlideshow(array);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
            if (array != IntPtr.Zero) Marshal.Release(array);
        }
    }

    public void Dispose()
    {
        if (Marshal.IsComObject(_wp)) Marshal.ReleaseComObject(_wp);
    }
}
