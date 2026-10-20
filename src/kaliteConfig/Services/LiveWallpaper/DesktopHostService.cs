using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using kaliteConfig.Services.Personalize.Native;

namespace kaliteConfig.Services.LiveWallpaper;

public sealed record MonitorRect(string Id, int X, int Y, int Width, int Height);

public interface IDesktopHostService
{
    IReadOnlyList<MonitorRect> GetMonitors();
    bool CanAttach(out string reason);
    bool TryAttach(IntPtr host, MonitorRect area, out string? failReason);
    bool Verify(IntPtr host, MonitorRect area);
    void Detach(IntPtr host);
    int RealBuildNumber();
}

public sealed class DesktopHostService : IDesktopHostService
{
    private const int Win11_24H2 = 26100;

    public int RealBuildNumber() => DesktopHostNative.RealBuildNumber();

    public bool CanAttach(out string reason)
    {
        int build = DesktopHostNative.RealBuildNumber();
        if (build == 0)
        {
            reason = "The Windows build number could not be read, so live wallpaper stays off.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public IReadOnlyList<MonitorRect> GetMonitors()
    {
        var list = new List<MonitorRect>();
        try
        {
            DesktopHostNative.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, ref DesktopHostNative.RECT rect, IntPtr data) =>
            {
                var info = new DesktopHostNative.MONITORINFO { cbSize = (uint)Marshal.SizeOf<DesktopHostNative.MONITORINFO>() };
                if (DesktopHostNative.GetMonitorInfoW(hMon, ref info))
                {
                    var r = info.rcMonitor;
                    list.Add(new MonitorRect(
                        hMon.ToInt64().ToString(),
                        r.Left, r.Top,
                        Math.Max(1, r.Right - r.Left),
                        Math.Max(1, r.Bottom - r.Top)));
                }
                return true;
            };
            DesktopHostNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
        }
        catch { }
        if (list.Count == 0)
            list.Add(new MonitorRect("primary", 0, 0, 1920, 1080));
        return list;
    }

    public bool TryAttach(IntPtr host, MonitorRect area, out string? failReason)
    {
        failReason = null;
        if (host == IntPtr.Zero)
        {
            failReason = "The host window has no handle.";
            return false;
        }
        try
        {
            var style = DesktopHostNative.GetWindowExStyle(host).ToInt64();
            style |= DesktopHostNative.WS_EX_TRANSPARENT | DesktopHostNative.WS_EX_NOACTIVATE | DesktopHostNative.WS_EX_TOOLWINDOW;
            DesktopHostNative.SetWindowExStyle(host, new IntPtr(style));
        }
        catch { }
        try
        {
            long style = DesktopHostNative.GetWindowStyle(host).ToInt64();
            style &= ~DesktopHostNative.WS_POPUP;
            style &= ~DesktopHostNative.WS_CAPTION;
            style &= ~DesktopHostNative.WS_THICKFRAME;
            style |= DesktopHostNative.WS_CHILD;
            DesktopHostNative.SetWindowStyle(host, new IntPtr(style));
        }
        catch { }
        IntPtr parent = FindWallpaperLayer();
        if (parent == IntPtr.Zero)
        {
            failReason = "The desktop wallpaper layer could not be found.";
            return false;
        }
        try
        {
            DesktopHostNative.ShowWindow(host, DesktopHostNative.SW_HIDE);
            DesktopHostNative.GetWindowRect(parent, out var pr);
            int x = area.X - pr.Left;
            int y = area.Y - pr.Top;
            if (DesktopHostNative.SetParent(host, parent) == IntPtr.Zero)
            {
                failReason = "The host window could not be placed behind the desktop icons.";
                return false;
            }
            IntPtr defView = DesktopHostNative.FindWindowExW(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            uint flags = DesktopHostNative.SWP_NOACTIVATE | DesktopHostNative.SWP_SHOWWINDOW | DesktopHostNative.SWP_FRAMECHANGED;
            if (defView == IntPtr.Zero)
                flags |= DesktopHostNative.SWP_NOZORDER;
            DesktopHostNative.SetWindowPos(host, defView, x, y, area.Width, area.Height, flags);
        }
        catch
        {
            failReason = "The host window could not be placed behind the desktop icons.";
            return false;
        }
        if (!VerifyDetail(host, area, out string verifyDetail))
        {
            failReason = "Placement verification failed (" + verifyDetail + "), so the host was removed.";
            Detach(host);
            return false;
        }
        return true;
    }

    public bool Verify(IntPtr host, MonitorRect area)
    {
        return VerifyDetail(host, area, out _);
    }

    public bool VerifyDetail(IntPtr host, MonitorRect area, out string detail)
    {
        detail = string.Empty;
        try
        {
            if (host == IntPtr.Zero) { detail = "host handle is zero"; return false; }
            if (!DesktopHostNative.IsWindowVisible(host)) { detail = "host window is not visible"; return false; }
            IntPtr parent = DesktopHostNative.GetParent(host);
            if (parent == IntPtr.Zero) { detail = "host has no parent"; return false; }
            if (!DesktopHostNative.IsChild(parent, host)) { detail = "host is not a child of its parent"; return false; }
            if (!DesktopHostNative.GetWindowRect(host, out var r)) { detail = "host rectangle could not be read"; return false; }
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w < area.Width - 8 || h < area.Height - 8) { detail = $"host is {w}x{h}, expected {area.Width}x{area.Height}"; return false; }
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }

    public void Detach(IntPtr host)
    {
        try
        {
            if (host == IntPtr.Zero) return;
            if (!DesktopHostNative.IsWindow(host)) return;
            try { DesktopHostNative.ShowWindow(host, DesktopHostNative.SW_HIDE); } catch { }
            try
            {
                IntPtr parent = DesktopHostNative.GetParent(host);
                if (parent != IntPtr.Zero)
                {
                    try { DesktopHostNative.SetParent(host, IntPtr.Zero); } catch { }
                }
            }
            catch { }
            try
            {
                if (!DesktopHostNative.IsWindow(host)) return;
                long style = DesktopHostNative.GetWindowStyle(host).ToInt64();
                if ((style & DesktopHostNative.WS_CHILD) != 0)
                {
                    style &= ~DesktopHostNative.WS_CHILD;
                    style |= DesktopHostNative.WS_POPUP;
                    try { DesktopHostNative.SetWindowStyle(host, new IntPtr(style)); } catch { }
                    try
                    {
                        DesktopHostNative.SetWindowPos(host, IntPtr.Zero, 0, 0, 0, 0,
                            DesktopHostNative.SWP_NOZORDER | DesktopHostNative.SWP_NOMOVE |
                            DesktopHostNative.SWP_NOSIZE | DesktopHostNative.SWP_NOACTIVATE |
                            DesktopHostNative.SWP_HIDEWINDOW | DesktopHostNative.SWP_FRAMECHANGED);
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch { }
    }

    private static IntPtr FindWallpaperLayer()
    {
        try
        {
            IntPtr progman = DesktopHostNative.FindWindowW("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            DesktopHostNative.SendMessageTimeoutW(progman, DesktopHostNative.SpawnWorkerMessage,
                UIntPtr.Zero, IntPtr.Zero, DesktopHostNative.SMTO_NORMAL, 1000, out _);
            if (DesktopHostNative.RealBuildNumber() >= Win11_24H2) return progman;
            IntPtr defViewHost = IntPtr.Zero;
            DesktopHostNative.EnumWindowsProc finder = (IntPtr hWnd, IntPtr param) =>
            {
                if (DesktopHostNative.FindWindowExW(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    defViewHost = hWnd;
                    return false;
                }
                return true;
            };
            DesktopHostNative.EnumWindows(finder, IntPtr.Zero);
            GC.KeepAlive(finder);
            if (defViewHost == IntPtr.Zero) return IntPtr.Zero;
            IntPtr worker = DesktopHostNative.FindWindowExW(IntPtr.Zero, defViewHost, "WorkerW", null);
            return worker;
        }
        catch { return IntPtr.Zero; }
    }
}
