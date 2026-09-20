using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

/// <summary>
/// Global snip hotkey on a dedicated hidden message window. Default PrtScn.
/// </summary>
public sealed class SnipHotkeyService : IDisposable
{
    public const uint ModNone = 0x0;
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    public const uint VkSnapshot = 0x2C;
    public const uint VkS = 0x53; // 'S' key

    /// <summary>Index of the "Custom…" slot: presets[0..3] are fixed, this one is user-captured.</summary>
    public const int CustomIndex = 4;

    /// <summary>True for keys that can be the main key of a hotkey (not a bare modifier).</summary>
    public static bool IsCapturableKey(uint vk) => vk switch
    {
        0x00 => false,
        0x10 or 0x11 or 0x12 => false, // Shift, Control, Alt
        0x5B or 0x5C => false,         // Left/Right Windows (opens Start; not capturable here)
        0x14 => false,                 // CapsLock
        0x90 or 0x91 or 0x92 => false, // NumLock/ScrollLock
        _ => true,
    };

    /// <summary>"Ctrl+Shift+S" style label for any modifiers + virtual key.</summary>
    public static string FormatLabel(uint modifiers, uint vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        VkSnapshot => "PrtScn",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),   // A-Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),   // 0-9
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),   // F1-F24
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        _ => $"VK {vk:X2}",
    };

    public sealed class HotkeyPreset
    {
        public HotkeyPreset(string label, uint modifiers, uint vk)
        {
            Label = label;
            Modifiers = modifiers;
            Vk = vk;
        }

        public string Label { get; }
        public uint Modifiers { get; }
        public uint Vk { get; }
        public override string ToString() => Label;
    }

    public static readonly HotkeyPreset[] Presets = new[]
    {
        new HotkeyPreset("PrtScn", ModNone, VkSnapshot),
        new HotkeyPreset("Ctrl+PrtScn", ModControl, VkSnapshot),
        new HotkeyPreset("Shift+PrtScn", ModShift, VkSnapshot),
        new HotkeyPreset("Ctrl+Shift+S", ModControl | ModShift, VkS),
    };

    private const int HotkeyId = 0xB002;
    private const uint WM_HOTKEY = 0x0312;

    public event Action? Pressed;

    private IntPtr _hwnd = IntPtr.Zero;
    private WndProcDelegate? _wndProc;
    private bool _registered;
    private bool _disposed;

    public bool IsRegistered => _registered;
    public HotkeyPreset Current { get; private set; } = Presets[0];

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private const string ClassName = "kaliteConfig_SnipHotkey";

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProc = WndProc;
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        ushort atom = RegisterClassExW(ref cls);
        if (atom == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = class already exists
            throw new InvalidOperationException("Could not register the snip hotkey window class.");
        _hwnd = CreateWindowExW(0, ClassName, "kaliteSnip Hotkey", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the snip hotkey window.");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            try { Pressed?.Invoke(); } catch { }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Registers the preset. Returns (false, message) when the key is taken.</summary>
    public (bool Ok, string Message) Register(HotkeyPreset preset)
    {
        if (!IsCapturableKey(preset.Vk))
            return (false, $"{preset.Label} needs a main key, not a bare modifier.");
        try
        {
            Unregister();
            EnsureWindow();
            if (!RegisterHotKey(_hwnd, HotkeyId, preset.Modifiers, preset.Vk))
            {
                int err = Marshal.GetLastWin32Error();
                return (false,
                    $"{preset.Label} is already taken by another app (Win32 error {err}). " +
                    "Pick a different hotkey below. Tip: if using PrtScn, disable 'Use the Print screen key to open Snipping Tool' in Windows Settings -> Accessibility -> Keyboard.");
            }
            _registered = true;
            Current = preset;
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Hotkey registration failed: {ex.Message}");
        }
    }

    public void Unregister()
    {
        try
        {
            if (_hwnd != IntPtr.Zero && _registered)
                UnregisterHotKey(_hwnd, HotkeyId);
        }
        catch { }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        try
        {
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            UnregisterClassW(ClassName, GetModuleHandleW(null));
        }
        catch { }
        _hwnd = IntPtr.Zero;
        _wndProc = null;
    }
}
