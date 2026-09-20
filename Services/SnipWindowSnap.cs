using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

// DWM window rectangles for click-to-select-window. Used ONLY when a press is
// released without a drag (under the click threshold): there is deliberately
// no hover highlight and no pre-made box.
public static class SnipWindowSnap
{
    public readonly struct ScreenRect
    {
        public readonly int Left, Top, Right, Bottom;
        public ScreenRect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_MINIMIZE = 0x20000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Topmost-first window rects in screen pixels, excluding ourselves,
    /// invisible/minimized/tool windows and empty rects.</summary>
    public static List<ScreenRect> GetWindowRects(IntPtr excludeHwnd)
    {
        var list = new List<ScreenRect>();
        try
        {
            EnumWindows((h, _) =>
            {
                try
                {
                    if (h == excludeHwnd || !IsWindowVisible(h)) return true;
                    int style = GetWindowLong(h, GWL_STYLE);
                    if ((style & WS_MINIMIZE) != 0) return true;
                    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                    if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;
                    if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
                    list.Add(new ScreenRect(r.Left, r.Top, r.Right, r.Bottom));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }
}
