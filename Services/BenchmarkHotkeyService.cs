using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

/// <summary>
/// Global benchmark hotkey on a dedicated hidden message window (same pattern
/// as TrayIconService — never subclasses the WinUI window). Default Ctrl+F10.
/// Registration failure (key taken) surfaces as a message, never silently.
/// </summary>
public sealed class BenchmarkHotkeyService : IDisposable
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;

    public const uint VkF10 = 0x79;
    public const uint VkF11 = 0x7A;
    public const uint VkF12 = 0x7B;

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
        new HotkeyPreset("Ctrl+F10", ModControl, VkF10),
        new HotkeyPreset("Ctrl+F11", ModControl, VkF11),
        new HotkeyPreset("Ctrl+F12", ModControl, VkF12),
        new HotkeyPreset("Alt+F10", ModAlt, VkF10),
    };

    private const int HotkeyId = 0xB001;
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

    private const string ClassName = "kaliteConfig_BenchHotkey";

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
            throw new InvalidOperationException("Could not register the hotkey window class.");
        _hwnd = CreateWindowExW(0, ClassName, "kaliteBench Hotkey", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the hotkey window.");
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
        try
        {
            Unregister();
            EnsureWindow();
            if (!RegisterHotKey(_hwnd, HotkeyId, preset.Modifiers, preset.Vk))
            {
                int err = Marshal.GetLastWin32Error();
                return (false,
                    $"{preset.Label} is already taken by another app (Win32 error {err}). " +
                    "Pick a different hotkey below.");
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
