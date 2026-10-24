using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Input.Interop;

internal static partial class MouseSystemInterop
{
    internal const uint SpiGetMouseSpeed = 0x0070;
    internal const uint SpiSetMouseSpeed = 0x0071;
    internal const uint SpiGetMouse = 0x0003;
    internal const uint SpiSetMouse = 0x0004;
    internal const uint SpiGetWheelScrollLines = 0x0068;
    internal const uint SpiSetWheelScrollLines = 0x0069;
    internal const uint SpiGetWheelScrollChars = 0x006C;
    internal const uint SpiSetWheelScrollChars = 0x006D;
    internal const uint SpiGetSnapToDefault = 0x005F;
    internal const uint SpiSetSnapToDefault = 0x0060;
    internal const uint SpiGetClickLock = 0x101E;
    internal const uint SpiSetClickLock = 0x101F;
    internal const uint SpiGetClickLockTime = 0x2008;
    internal const uint SpiSetClickLockTime = 0x2009;
    internal const uint SpiFlagsUpdateAndBroadcast = 0x0001 | 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(
        uint uiAction,
        uint uiParam,
        IntPtr pvParam,
        uint fWinIni);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SwapMouseButton([MarshalAs(UnmanagedType.Bool)] bool swap);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDoubleClickTime(uint interval);

    internal const int SmSwapButton = 23;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetSystemMetrics(int index);
}
