// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

/// <summary>
/// Bindings for the standard Windows monitor control path: EnumDisplayDevices to find a
/// display's interface name, then the DxgkDDI physical-monitor API to read and write DDC/CI
/// VCP features.
///
/// This replaces a direct P/Invoke into nvapi64.dll. That approach cannot work: on current
/// NVIDIA drivers System32\nvapi64.dll is a forwarding stub with no individually exported
/// NvAPI_* symbols, so every NvAPI call has to go through the single dispatcher entry that
/// the NvAPIWrapper package already owns. DDC/CI is also the mechanism the OS itself uses
/// for monitor brightness, and unlike a raw driver call it cannot fault the process.
/// </summary>
internal static partial class NativeMethods
{
    internal static class DdcCi
    {
        private const string User32 = "user32.dll";
        private const string Dxva2 = "dxva2.dll";

        /// <summary>Requests the display interface name (<c>\\?\DISPLAY#...</c>) from EnumDisplayDevices.</summary>
        internal const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;


        // DDC/CI standard VCP feature codes.
        internal const byte VCP_BRIGHTNESS = 0x10;
        internal const byte VCP_CONTRAST = 0x12;
        internal const byte VCP_RED_GAIN = 0x16;
        internal const byte VCP_GREEN_GAIN = 0x18;
        internal const byte VCP_BLUE_GAIN = 0x1A;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public int Cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport(User32, EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool EnumDisplayDevices(
            IntPtr lpcDevice, string lpcDeviceString, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport(Dxva2, SetLastError = true)]
        internal static extern bool DestroyPhysicalMonitor(IntPtr hPhysicalMonitor);

        [DllImport(Dxva2, SetLastError = true)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hPhysicalMonitor, byte bVCPCode, out uint lpdwCurrentValue, out uint lpdwMaximumValue);

        [DllImport(Dxva2, SetLastError = true)]
        internal static extern bool SetVCPFeature(IntPtr hPhysicalMonitor, byte bVCPCode, uint dwValue);

        [DllImport(Dxva2, SetLastError = true)]
        internal static extern bool GetCapabilitiesFromMonitor(
            IntPtr hPhysicalMonitor, uint dwCapabilities, out uint pdwCapabilities);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PHYSICAL_MONITOR
        {
            public IntPtr Handle;
            // The description is a fixed 128-wchar buffer. It is declared as raw bytes rather
            // than a string so nothing has to be allocated or freed across the boundary.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] DescriptionUtf16;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport(User32, SetLastError = true)]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport(User32, SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint dwPhysicalMonitorArraySize, ref PHYSICAL_MONITOR pPhysicalMonitorArray);

        /// <summary>DDC/CI presence bit reported in the monitor's capability block.</summary>
        internal const uint CAP_DDC_CI = 0x00000007;

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>
        /// Opens the physical monitor for the display at a desktop origin.
        ///
        /// This deliberately avoids EnumDisplayDevices. That API is unavailable outside an
        /// interactive session, so a display-interface-name route built on it reports
        /// "no DDC/CI" even where the hardware does support it. The CCD snapshot already knows
        /// where each display sits on the desktop, so MonitorFromPoint gets the HMONITOR
        /// directly.
        ///
        /// The origin is the display's top-left corner, which is on the boundary of the
        /// monitor's area. Two points are tried in turn — the corner and then the centre of
        /// the same display — because a corner that rounds to the neighbouring monitor on a
        /// scaled desktop would otherwise silently address the wrong panel, and a write to
        /// the wrong panel is worse than no write at all.
        ///
        /// The descriptor is passed by ref as a single element rather than as an array, and
        /// DestroyPhysicalMonitors is never called: marshalling an array of these across the
        /// boundary is what faulted the process earlier.
        /// </summary>
        internal static bool TryOpenPhysicalMonitor(int originX, int originY, out IntPtr monitorHandle)
            => TryOpenPhysicalMonitor(originX, originY, 0, 0, out monitorHandle);
        /// <summary>
        /// <see cref="TryOpenPhysicalMonitor(int,int,out IntPtr)"/> with the display's size,
        /// which lets a centre point be derived. The size is only a hint; a zero or
        /// implausible one falls back to the corner-only behaviour.
        /// </summary>
        internal static bool TryOpenPhysicalMonitor(int originX, int originY, int width, int height,
            out IntPtr monitorHandle)
        {
            monitorHandle = IntPtr.Zero;

            if (TryOpenFromPoint(originX, originY, out monitorHandle)) return true;

            if (width > 0 && height > 0 &&
                TryOpenFromPoint(originX + width / 2, originY + height / 2, out monitorHandle))
                return true;

            return TryOpenPhysicalMonitorByEnumeration(originX, originY, width, height, out monitorHandle);
        }

        private static bool TryOpenFromPoint(int x, int y, out IntPtr monitorHandle)
        {
            monitorHandle = IntPtr.Zero;

            IntPtr hMonitor;
            try
            {
                hMonitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            }
            catch { return false; }

            if (hMonitor == IntPtr.Zero) return false;
            return TryGetPhysicalMonitor(hMonitor, out monitorHandle);
        }

        /// <summary>
        /// Last route: walk the monitor enumeration rather than trusting a point.
        ///
        /// GetPhysicalMonitorsFromHMONITOR is documented to reject an HMONITOR that did
        /// not come from the enumeration or point APIs, and it reports that case by
        /// succeeding with a null handle — which reads as "this display has no monitor",
        /// even though one is attached. Walking EnumDisplayMonitors and matching the
        /// monitor rectangle against the display's own position means the handle handed to
        /// it is the enumerated one, and the rectangle comparison keeps this from ever
        /// opening a neighbouring panel.
        /// </summary>
        internal static bool TryOpenPhysicalMonitorByEnumeration(int originX, int originY, int width, int height,
            out IntPtr monitorHandle)
        {
            monitorHandle = IntPtr.Zero;

            var found = IntPtr.Zero;
            var candidates = new System.Collections.Generic.List<IntPtr>();

            MonitorEnumProc callback = (IntPtr h, IntPtr dc, IntPtr rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref info))
                {
                    // Half-open containment: the right/bottom edges belong to the next
                    // monitor along, so a display's own right edge is not a match.
                    if (originX >= info.rcMonitor.Left && originX < info.rcMonitor.Right &&
                        originY >= info.rcMonitor.Top && originY < info.rcMonitor.Bottom)
                    {
                        found = h;
                        return false;   // stop enumerating
                    }
                }
                candidates.Add(h);
                return true;
            };

            try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero); }
            catch { /* fall through to the candidates collected so far */ }

            if (found == IntPtr.Zero && candidates.Count == 0) return false;

            // No rectangle matched (a scaled desktop can put the reported position
            // outside every monitor), so try each enumerated monitor once.
            var order = found != IntPtr.Zero
                ? new[] { found }
                : candidates.ToArray();

            foreach (var h in order)
            {
                if (TryGetPhysicalMonitor(h, out monitorHandle)) return true;
            }

            monitorHandle = IntPtr.Zero;
            return false;
        }

        /// <summary>
        /// Fetches the first physical monitor behind an HMONITOR.
        ///
        /// The call succeeding is not the same as the monitor being there: a stack that
        /// cannot map the HMONITOR to hardware returns true with a null handle, so the
        /// handle is what decides success.
        /// </summary>
        private static bool TryGetPhysicalMonitor(IntPtr hMonitor, out IntPtr monitorHandle)
        {
            monitorHandle = IntPtr.Zero;
            if (hMonitor == IntPtr.Zero) return false;

            var physical = new PHYSICAL_MONITOR
            {
                Handle = IntPtr.Zero,
                DescriptionUtf16 = new byte[256],
            };

            bool ok;
            try
            {
                ok = GetPhysicalMonitorsFromHMONITOR(hMonitor, 1, ref physical);
            }
            catch { return false; }

            int error = Marshal.GetLastWin32Error();
            if (!ok || physical.Handle == IntPtr.Zero)
            {
                return false;
            }

            monitorHandle = physical.Handle;
            return true;
        }

        private static int SizeOf<T>() => Marshal.SizeOf<T>();

        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport(User32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        /// <summary>Asks the monitor whether it implements DDC/CI at all (capability 0x07).</summary>
        internal static bool TryGetDdcCiCapability(IntPtr monitorHandle, out uint capability)
        {
            capability = 0;
            try { return GetCapabilitiesFromMonitor(monitorHandle, CAP_DDC_CI, out capability); }
            catch { return false; }
        }


        internal static void ClosePhysicalMonitor(IntPtr monitorHandle)
        {
            if (monitorHandle != IntPtr.Zero)
            {
                try { DestroyPhysicalMonitor(monitorHandle); } catch { }
            }
        }

        internal static bool TryGetVcp(IntPtr monitorHandle, byte vcpCode, out uint current, out uint maximum)
        {
            current = 0;
            maximum = 0;
            try
            {
                return GetVCPFeatureAndVCPFeatureReply(monitorHandle, vcpCode, out current, out maximum);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool TrySetVcp(IntPtr monitorHandle, byte vcpCode, uint value)
        {
            try
            {
                return SetVCPFeature(monitorHandle, vcpCode, value);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
