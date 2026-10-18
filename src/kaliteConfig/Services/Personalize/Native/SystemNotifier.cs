using System;

namespace kaliteConfig.Services.Personalize.Native;

internal static class SystemNotifier
{
    private const uint TimeoutMs = 1000;

    public static bool BroadcastSettingChange(string area)
    {
        IntPtr result = NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST,
            NativeMethods.WM_SETTINGCHANGE,
            UIntPtr.Zero,
            area,
            NativeMethods.SMTO_ABORTIFHUNG,
            TimeoutMs,
            out _);
        return result != IntPtr.Zero;
    }

    public static bool BroadcastDwmColorChanged()
    {
        IntPtr result = NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST,
            NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED,
            UIntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG,
            TimeoutMs,
            out _);
        return result != IntPtr.Zero;
    }
}
