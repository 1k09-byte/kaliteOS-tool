using System;
using kaliteConfig.Services.Personalize.Native;

namespace kaliteConfig.Services.Personalize;

public sealed class WindowsCapabilityService
{
    public WindowsCapabilityService()
    {
        BuildNumber = ReadBuildNumber();
    }

    // Real build from RtlGetVersion, not the compatibility-shimmed Environment value.
    public int BuildNumber { get; }

    // Windows 11 starts at build 22000 (Microsoft Windows 11 build numbering).
    public bool IsWindows11 => BuildNumber >= 22000;

    // The project ships as an unpackaged WinUI 3 app (csproj). Registry writes are
    // therefore not redirected or virtualized by MSIX.
    public bool IsPackaged => false;

    // Read-only check. True when a policy key under HKCU has any value.
    // PERSONALIZE-REVIEW: policy detection is a heuristic on well-known HKCU policy keys.
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
