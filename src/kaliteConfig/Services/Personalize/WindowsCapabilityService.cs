using System;
using kaliteConfig.Services.Personalize.Native;

namespace kaliteConfig.Services.Personalize;

public sealed class WindowsCapabilityService
{
    public WindowsCapabilityService()
    {
        BuildNumber = ReadBuildNumber();
    }

    public int BuildNumber { get; }

    public bool IsWindows11 => BuildNumber >= 22000;

    public bool IsPackaged => false;

    public bool IsPolicyLocked(string policySubKey)
    {
        try
        {
            return HkcuRegistry.HasAnyValue(policySubKey);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool IsHighContrastActive()
    {
        var hc = new NativeMethods.HIGHCONTRAST { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.HIGHCONTRAST>() };
        return NativeMethods.SystemParametersInfoHighContrast(NativeMethods.SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0)
            && (hc.dwFlags & NativeMethods.HCF_HIGHCONTRASTON) != 0;
    }

    private static int ReadBuildNumber()
    {
        var info = new NativeMethods.OSVERSIONINFOEX
        {
            dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.OSVERSIONINFOEX>(),
        };
        return NativeMethods.RtlGetVersion(ref info) == 0 ? (int)info.dwBuildNumber : 0;
    }
}
