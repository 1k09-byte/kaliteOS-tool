using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services.Personalize.Native;

internal static class NativeMethods
{
    public const uint SPI_GETMOUSE = 0x0003;
    public const uint SPI_SETMOUSE = 0x0004;
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPI_SETCURSORS = 0x0057;
    // Undocumented: applies CursorBaseSize live (pvParam = pixel size).
    public const uint SPI_SETCURSORSIZE = 0x2029;
    public const uint SPI_GETMOUSESPEED = 0x0070;
    public const uint SPI_SETMOUSESPEED = 0x0071;
    public const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    public const uint SPI_SETMOUSETRAILS = 0x005D;
    public const uint SPI_SETCARETWIDTH = 0x2007;
    public const uint SPI_GETHIGHCONTRAST = 0x0042;
    public const uint HCF_HIGHCONTRASTON = 0x00000001;

    public const uint SPIF_UPDATEINIFILE = 0x0001;
    public const uint SPIF_SENDCHANGE = 0x0002;

    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public static readonly IntPtr HWND_BROADCAST = new(0xffff);

    public const int COLOR_BACKGROUND = 1;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoPtr(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoIntArray(uint uiAction, uint uiParam, [In, Out] int[] pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoString(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoRef(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    public static extern bool SystemParametersInfoHighContrast(uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetSysColors(int cElements, int[] lpaElements, uint[] lpaRgbValues);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OSVERSIONINFOEX
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll")]
    internal static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    public const uint IMAGE_CURSOR = 2;
    public const uint LR_LOADFROMFILE = 0x00000010;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IntPtr ppsiItemArray);
}
