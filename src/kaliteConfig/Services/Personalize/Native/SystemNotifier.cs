using System;

namespace kaliteConfig.Services.Personalize.Native;

internal static class SystemNotifier
{
    private const uint TimeoutMs = 1000;

    // WM_SETTINGCHANGE with an area name, e.g. "ImmersiveColorSet" for the app and
    // system theme. Returns false if no window answered within the timeout.
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

    // Tells DWM the accent color changed, so title bars redraw.
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
