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
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

/// <summary>
/// Win32 P/Invoke declarations for Display Configuration (CCD),
/// EnumDisplaySettingsEx/ChangeDisplaySettingsEx, and Advanced Color (HDR).
/// </summary>
internal static partial class NativeMethods
{
    internal static partial class Display
    {
        // ──────────────────────────────────────────────────────────────
        //  QueryDisplayConfig / SetDisplayConfig  (CCD)
        // ──────────────────────────────────────────────────────────────

        internal const uint QDC_ALL_PATHS                = 0x00000001;
        internal const uint QDC_ONLY_ACTIVE_PATHS        = 0x00000002;
        internal const uint QDC_DATABASE_CURRENT         = 0x00000004;

        internal const uint SDC_APPLY                    = 0x00000080;
        internal const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
        internal const uint SDC_SAVE_TO_DATABASE         = 0x00000200;
        internal const uint SDC_VALIDATE                 = 0x00000040;
        internal const uint SDC_ALLOW_CHANGES            = 0x00000400;
        internal const uint SDC_TOPOLOGY_SUPPLIED        = 0x00000008;
        internal const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            out DISPLAYCONFIG_TOPOLOGY_ID currentTopologyId);

        // Overload without topology (for QDC_ALL_PATHS which doesn't return topology)
        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        internal static extern int SetDisplayConfig(
            uint numPathArrayElements,
            [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
            uint numModeInfoArrayElements,
            [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE colorState);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET dpiScale);

        [DllImport("user32.dll")]
        internal static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET dpiScale);

        // ──────────────────────────────────────────────────────────────
        //  CCD Structs
        // ──────────────────────────────────────────────────────────────

        internal enum DISPLAYCONFIG_TOPOLOGY_ID : uint
        {
            Internal  = 0x00000001,
            Clone     = 0x00000002,
            Extend    = 0x00000004,
            External  = 0x00000008,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID AdapterId;
            public uint Id;           // source index
            public uint ModeInfoIdx;  // index into mode info array (or DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID AdapterId;
            public uint Id;            // target index
            public uint ModeInfoIdx;
            public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY OutputTechnology;
            public DISPLAYCONFIG_ROTATION Rotation;
            public DISPLAYCONFIG_SCALING Scaling;
            public DISPLAYCONFIG_RATIONAL RefreshRate;
            public DISPLAYCONFIG_SCANLINE_ORDERING ScanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
            public uint Flags;
        }

        internal const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;

        internal enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
        {
            Other        = unchecked((uint)-1),
            Hd15         = 0,
            Svideo       = 1,
            CompositeVideo = 2,
            ComponentVideo = 3,
            Dvi          = 4,
            Hdmi         = 5,
            Lvds         = 6,
            Djpn         = 8,
            Sdi          = 9,
            DisplayportExternal = 10,
            DisplayportEmbedded = 11,
            UdiExternal  = 12,
            UdiEmbedded  = 13,
            Sdtvdongle   = 14,
            Miracast     = 15,
            IndirectWired = 16,
            IndirectVirtual = 17,
            Internal     = unchecked(0x80000000),
        }

        internal enum DISPLAYCONFIG_ROTATION : uint
        {
            Identity  = 1,
            Rotate90  = 2,
            Rotate180 = 3,
            Rotate270 = 4,
        }

        internal enum DISPLAYCONFIG_SCALING : uint
        {
            Identity             = 1,
            Centered             = 2,
            Stretched            = 3,
            AspectRatioCenteredMax = 4,
            Custom               = 5,
            Preferred            = 128,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        internal enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
        {
            Unspecified          = 0,
            Progressive          = 1,
            Interlaced           = 2,
            InterlacedUpperFieldFirst = Interlaced,
            InterlacedLowerFieldFirst = 3,
        }

        internal enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
        {
            Source = 1,
            Target = 2,
            DesktopImage = 3,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_2DREGION
        {
            public uint Cx;
            public uint Cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong PixelRate;
            public DISPLAYCONFIG_RATIONAL HSyncFreq;
            public DISPLAYCONFIG_RATIONAL VSyncFreq;
            public DISPLAYCONFIG_2DREGION ActiveSize;
            public DISPLAYCONFIG_2DREGION TotalSize;
            public uint VideoStandard;
            public DISPLAYCONFIG_SCANLINE_ORDERING ScanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO TargetVideoSignalInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINTL
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint Width;
            public uint Height;
            public uint PixelFormat;  // DISPLAYCONFIG_PIXELFORMAT
            public POINTL Position;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct DISPLAYCONFIG_MODE_INFO_UNION
        {
            [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE TargetMode;
            [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE SourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_MODE_INFO
        {
            public DISPLAYCONFIG_MODE_INFO_TYPE InfoType;
            public uint Id;
            public LUID AdapterId;
            public DISPLAYCONFIG_MODE_INFO_UNION Info;
        }

        // ──────────────────────────────────────────────────────────────
        //  DisplayConfigGetDeviceInfo helpers
        // ──────────────────────────────────────────────────────────────

        internal enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
        {
            GetSourceName         = 1,
            GetTargetName         = 2,
            GetTargetPreferredMode = 3,
            GetAdapterName        = 4,
            SetTargetPersistence  = 5,
            GetTargetBaseType     = 6,
            GetSupportVirtualResolution = 7,
            SetSupportVirtualResolution = 8,
            GetAdvancedColorInfo  = 9,
            SetAdvancedColorState = 10,
            GetSdrWhiteLevel     = 11,

            // Per-source DPI scaling. These two are not in wingdi.h: the values are
            // negative, so they only round-trip through a uint-typed enum that is written
            // with unchecked((uint)-3) / unchecked((uint)-4). This is the same undocumented
            // pair the Settings app itself uses, and it is the only way to change a
            // monitor's scale without a sign-out.
            GetDpiScale           = unchecked((uint)-3),
            SetDpiScale           = unchecked((uint)-4),
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE Type;
            public uint Size;
            public LUID AdapterId;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS Flags;
            public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY OutputTechnology;
            public ushort EdidManufactureId;
            public ushort EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string MonitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string MonitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
        {
            public uint Value;
            // bit 0 = friendlyNameFromEdid
            // bit 1 = friendlyNameForced
            // bit 2 = edidIdsValid
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ViewGdiDeviceName;
        }

        // ──────────────────────────────────────────────────────────────
        //  Advanced Color (HDR)
        // ──────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            public uint Value;  // bitfield: advancedColorSupported(1), advancedColorEnabled(1), wideColorEnforced(1), advancedColorForceDisabled(1)
            public uint ColorEncoding;
            public uint BitsPerColorChannel;

            public bool AdvancedColorSupported => (Value & 0x1) != 0;
            public bool AdvancedColorEnabled   => (Value & 0x2) != 0;
            public bool WideColorEnforced      => (Value & 0x4) != 0;
            public bool AdvancedColorForceDisabled => (Value & 0x8) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            public uint Value;  // bit 0 = enableAdvancedColor
        }

        // ──────────────────────────────────────────────────────────────────
        //  Per-source DPI scaling (undocumented; see the enum above)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reply to <see cref="DISPLAYCONFIG_DEVICE_INFO_TYPE.GetDpiScale"/>. The three values
        /// are step counts relative to the recommended scale for this source, not percentages:
        /// 0 means "recommended", -1 means one step down the list, and so on. The packet is
        /// 32 bytes, which is what the OS expects to be handed.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            public int MinScaleRel;
            public int CurScaleRel;
            public int MaxScaleRel;
        }

        /// <summary>
        /// Request for <see cref="DISPLAYCONFIG_DEVICE_INFO_TYPE.SetDpiScale"/>. 24 bytes.
        /// DPI scaling belongs to the *source*, so the header carries the source's adapter
        /// and source id rather than the target's.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
            public int ScaleRel;
        }

        // ──────────────────────────────────────────────────────────────
        //  EnumDisplaySettingsEx / ChangeDisplaySettingsEx
        // ──────────────────────────────────────────────────────────────

        internal const uint ENUM_CURRENT_SETTINGS  = unchecked((uint)-1);
        internal const uint ENUM_REGISTRY_SETTINGS = unchecked((uint)-2);

        internal const uint CDS_TEST            = 0x00000002;
        internal const uint CDS_UPDATEREGISTRY  = 0x00000001;
        internal const uint CDS_NORESET         = 0x10000000;
        internal const uint CDS_RESET           = 0x40000000;

        internal const int DISP_CHANGE_SUCCESSFUL  = 0;
        internal const int DISP_CHANGE_RESTART     = 1;
        internal const int DISP_CHANGE_BADMODE     = -2;
        internal const int DISP_CHANGE_FAILED      = -1;
        internal const int DISP_CHANGE_BADFLAGS    = -4;
        internal const int DISP_CHANGE_BADPARAM    = -5;
        internal const int DISP_CHANGE_NOTUPDATED  = -3;

        internal const uint DM_PELSWIDTH       = 0x00080000;
        internal const uint DM_PELSHEIGHT      = 0x00100000;
        internal const uint DM_DISPLAYFREQUENCY = 0x00400000;
        internal const uint DM_DISPLAYORIENTATION = 0x00000080;  // dmDisplayOrientation
        internal const uint DM_POSITION        = 0x00000020;

        internal const int DMDO_DEFAULT  = 0;
        internal const int DMDO_90       = 1;
        internal const int DMDO_180      = 2;
        internal const int DMDO_270      = 3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;

            // Union: printer or display
            public int dmPositionX;     // POINTL.x
            public int dmPositionY;     // POINTL.y
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;

            // ICM fields
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsExW(
            string? lpszDeviceName,
            uint iModeNum,
            ref DEVMODEW lpDevMode,
            uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        internal static extern int ChangeDisplaySettingsExW(
            string? lpszDeviceName,
            ref DEVMODEW lpDevMode,
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

        // Null DEVMODE overload (reset to registry default)
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        internal static extern int ChangeDisplaySettingsExW(
            string? lpszDeviceName,
            IntPtr lpDevMode,   // NULL
            IntPtr hwnd,
            uint dwflags,
            IntPtr lParam);

        // ──────────────────────────────────────────────────────────────
        //  Per-monitor DPI
        // ──────────────────────────────────────────────────────────────

        internal enum MONITOR_DPI_TYPE : uint
        {
            MDT_EFFECTIVE_DPI = 0,
            MDT_ANGULAR_DPI   = 1,
            MDT_RAW_DPI       = 2,
        }

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr hMonitor,
            MONITOR_DPI_TYPE dpiType,
            out uint dpiX,
            out uint dpiY);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINTL pt, uint dwFlags);

        internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    }
}
