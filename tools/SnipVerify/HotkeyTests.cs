using System;
using kaliteConfig.Services;

// Unit tests for custom-hotkey labels and key validation.
internal static class HotkeyTests
{
    public static void Run(Action<bool, string> check)
    {
        check(SnipHotkeyService.FormatLabel(0, SnipHotkeyService.VkSnapshot) == "PrtScn", "hotkey label bare PrtScn");
        check(SnipHotkeyService.FormatLabel(
            SnipHotkeyService.ModControl | SnipHotkeyService.ModShift, SnipHotkeyService.VkS) == "Ctrl+Shift+S", "hotkey label Ctrl+Shift+S");
        check(SnipHotkeyService.FormatLabel(SnipHotkeyService.ModAlt, 0x75) == "Alt+F6", "hotkey label Alt+F6");
        check(SnipHotkeyService.FormatLabel(SnipHotkeyService.ModControl, 0x42) == "Ctrl+B", "hotkey label Ctrl+B");
        check(SnipHotkeyService.FormatLabel(0, 0x2D) == "Insert", "hotkey label Insert");

        check(SnipHotkeyService.IsCapturableKey(0x42), "hotkey B capturable");
        check(SnipHotkeyService.IsCapturableKey(SnipHotkeyService.VkSnapshot), "hotkey PrtScn capturable");
        check(!SnipHotkeyService.IsCapturableKey(0x11), "hotkey bare Control rejected");
        check(!SnipHotkeyService.IsCapturableKey(0x10), "hotkey bare Shift rejected");
        check(!SnipHotkeyService.IsCapturableKey(0x12), "hotkey bare Alt rejected");
        check(!SnipHotkeyService.IsCapturableKey(0x5B), "hotkey Win rejected");
        check(!SnipHotkeyService.IsCapturableKey(0x00), "hotkey empty rejected");

        check(SnipHotkeyService.CustomIndex == SnipHotkeyService.Presets.Length, "hotkey custom slot follows presets");
    }
}
