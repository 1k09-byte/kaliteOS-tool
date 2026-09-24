using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

/// <summary>
/// System-tray icon via raw Win32 (Shell_NotifyIcon + a hidden message window).
/// No extra packages: double-click restores the window, right-click offers
/// Open / Exit. The window must route its close through this (hide instead).
///
/// Callback correctness: the callback message MUST come from
/// RegisterWindowMessage, not a raw WM_APP constant. WM_APP-range values are
/// per-window private conventions - every other tray icon on the system uses
/// one too, so a hardcoded WM_APP+1 made this window receive (and misread as
/// clicks) every tray message in the shell, flooding WndProc hundreds of
/// times per second and drowning the real right-click. A registered message
/// is globally unique, and wParam is additionally checked against uID.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public Action? OnOpen;
    public Action? OnExit;
    public event Action<SnipMode>? OnTakeScreenshot;

    public enum SnipMode
    {
        Region,
        Window,
        Fullscreen,
        Freeform,
        Delayed
    }

    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CLOSE = 0x0010;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    // Menu item IDs
    private const uint ID_VERSION = 99;
    private const uint ID_OPEN = 100;
    private const uint ID_TAKE_SNIP = 101;
    private const uint ID_SNIP_REGION = 102;
    private const uint ID_SNIP_WINDOW = 103;
    private const uint ID_SNIP_FULLSCREEN = 104;
    private const uint ID_SNIP_FREEFORM = 105;
    private const uint ID_SNIP_DELAYED = 106;
    private const uint ID_SEPARATOR1 = 107;
    private const uint ID_EXIT = 108;

    /// <summary>App version for the tray menu header (assembly version, no dots trimmed).</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>Per-process registered tray callback message, resolved once.</summary>
    private static uint? _callbackMsg;

    private static uint CallbackMsg => _callbackMsg ??= RegisterWindowMessageW("kaliteConfig_TrayCallback");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuW(IntPtr menu, uint position, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string messageName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _icon = IntPtr.Zero;
    private WndProcDelegate? _proc;
    private bool _added;
    private bool _disposed;
    private int _inCallback;
    private readonly string _className = "kaliteConfigTray" + Environment.ProcessId;

    internal static void Tlog(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kalite_tray.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public bool Show(string iconPath, string tooltip)
    {
        try
        {
            _proc = WndProc;
            var cls = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = _className,
            };
            if (RegisterClassExW(ref cls) == 0)
            {
                Tlog($"RegisterClassEx failed err={Marshal.GetLastWin32Error()}");
                return false;
            }
            // A hidden-but-VISIBLE popup parked off-screen (NOT a message-only
            // window): tray callbacks arrive and the window CAN take the
            // foreground - which TrackPopupMenu requires, or the menu closes
            // the instant it opens (observed: picked=0 two ms after enter).
            // WS_EX_NOACTIVATE was removed for exactly that reason: a
            // NOACTIVATE window cannot become foreground, so SetForegroundWindow
            // silently failed and the right-click menu never opened.
            // TOOLWINDOW keeps it out of Alt-Tab, Task View and the taskbar,
            // and at 1x1 off-screen it is invisible regardless.
            const uint WS_POPUP = 0x80000000;
            const uint WS_EX_TOOLWINDOW = 0x00000080;
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, _className, "kaliteConfigTray",
                WS_POPUP, -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Tlog($"CreateWindowEx failed err={Marshal.GetLastWin32Error()}");
                return false;
            }
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

            _icon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            Tlog($"hwnd=0x{_hwnd.ToInt64():X} icon=0x{_icon.ToInt64():X} iconPathExists={System.IO.File.Exists(iconPath)}");
            _added = AddIcon(tooltip);
            return _added;
        }
        catch (Exception ex) { Tlog("Show EX: " + ex.Message); return false; }
    }

    private bool AddIcon(string tooltip)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP | (_icon != IntPtr.Zero ? NIF_ICON : 0),
            uCallbackMessage = CallbackMsg,
            hIcon = _icon,
            szTip = tooltip.Length > 127 ? tooltip.Substring(0, 127) : tooltip,
        };
        bool ok = Shell_NotifyIconW(NIM_ADD, ref data);
        Tlog($"Shell_NotifyIcon ADD={ok} err={Marshal.GetLastWin32Error()} msg=0x{CallbackMsg:X}");
        return ok;
    }

    /// <summary>
    /// Updates the hover tooltip (NIM_MODIFY). Used for the persistent
    /// "currently active game profile" indicator - the user is usually not
    /// looking at the main window when an auto-switch fires, but the tray
    /// is always one hover away. No-op when the icon isn't up.
    /// </summary>
    public bool UpdateTooltip(string tooltip)
    {
        try
        {
            if (!_added || _hwnd == IntPtr.Zero) return false;
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_TIP,
                szTip = tooltip.Length > 127 ? tooltip.Substring(0, 127) : tooltip,
            };
            return Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch { return false; }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMsg)
            {
                // Identity check: registered messages are unique, but stay
                // defensive - only treat it as ours when the sender says uID 1.
                if (wParam != (IntPtr)1) return DefWindowProcW(hWnd, msg, wParam, lParam);

                uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                // Reentrancy guard: the menu modal loop (and any stray double
                // delivery) must not invoke handlers while one is in flight.
                if (System.Threading.Interlocked.CompareExchange(ref _inCallback, 1, 0) == 0)
                {
                    try
                    {
                        // WM_MOUSEMOVE floods while the cursor sits on the icon;
                        // it carries no action, so don't even log it.
                        if (mouse != 0x0200) Tlog($"callback mouse=0x{mouse:X}");
                        if (mouse == WM_LBUTTONDBLCLK || mouse == WM_LBUTTONUP)
                            OnOpen?.Invoke();
                        else if (mouse == WM_RBUTTONUP)
                            ShowMenu();
                    }
                    finally { _inCallback = 0; }
                }
            }
            else if (msg == WM_CLOSE)
            {
                // Hidden helper window: never let WM_CLOSE destroy it while
                // the notify icon still points here (a destroyed window makes
                // the icon a zombie: visible but permanently unclickable).
                return IntPtr.Zero;
            }
        }
        catch { }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        Tlog("ShowMenu enter");
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            // Version header: grayed, non-clickable. Tells the user which build
            // this tray icon belongs to.
            AppendMenuW(menu, MF_STRING | MF_GRAYED, ID_VERSION, "kaliteConfig v" + AppVersion);
            AppendMenuW(menu, MF_SEPARATOR, ID_SEPARATOR1, null);

            // Open kaliteConfig
            AppendMenuW(menu, MF_STRING, ID_OPEN, "Open kaliteConfig");

            // Take Screenshot with submenu
            IntPtr snipMenu = CreatePopupMenu();
            AppendMenuW(snipMenu, MF_STRING, ID_SNIP_REGION, "Region");
            AppendMenuW(snipMenu, MF_STRING, ID_SNIP_WINDOW, "Window");
            AppendMenuW(snipMenu, MF_STRING, ID_SNIP_FULLSCREEN, "Fullscreen");
            AppendMenuW(snipMenu, MF_STRING, ID_SNIP_FREEFORM, "Freeform");
            AppendMenuW(snipMenu, MF_STRING, ID_SNIP_DELAYED, "Delayed (3s)");
            AppendMenuW(menu, MF_POPUP, (nuint)snipMenu, "Take Screenshot");

            // Separator
            AppendMenuW(menu, MF_SEPARATOR, ID_SEPARATOR1, null);

            // Exit
            AppendMenuW(menu, MF_STRING, ID_EXIT, "Exit");

            if (!GetCursorPos(out POINT pt)) { Tlog("GetCursorPos failed"); return; }
            // TrackPopupMenu's window must be foreground when the menu opens,
            // or the first click outside (or the button-up from the right
            // click itself) cancels it and TrackPopupMenu returns 0.
            //
            // SetForegroundWindow alone FAILS here (observed: picked=0 two ms
            // after enter): a background process has no foreground rights, so
            // the call is silently denied and the menu dismisses instantly.
            // The standard workaround (Raymond Chen / KB9495115 pattern):
            // attach this thread's input queue to the foreground thread, make
            // ourselves foreground, run the menu, then detach. With the queues
            // attached the foreground transfer is permitted and the menu stays
            // open for a real click.
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try
            {
                SetForegroundWindow(_hwnd);
                uint picked = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd);
                // KB135788: hand the foreground back so the taskbar doesn't stay
                // stuck and the next tray click works first time.
                PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Tlog($"picked={picked} attached={attached}");
                HandleMenuSelection(picked);
                // If TrackPopupMenu was cancelled (picked==0) the foreground jump
                // leaves a focus hole; another foreground pass on our own window
                // clears it without stealing focus from anything else.
                if (picked == 0) SetForegroundWindow(_hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
        }
        finally { DestroyMenu(menu); }
    }

    private void HandleMenuSelection(uint picked)
    {
        switch (picked)
        {
            case ID_OPEN:
                OnOpen?.Invoke();
                break;
            case ID_SNIP_REGION:
                OnTakeScreenshot?.Invoke(SnipMode.Region);
                break;
            case ID_SNIP_WINDOW:
                OnTakeScreenshot?.Invoke(SnipMode.Window);
                break;
            case ID_SNIP_FULLSCREEN:
                OnTakeScreenshot?.Invoke(SnipMode.Fullscreen);
                break;
            case ID_SNIP_FREEFORM:
                OnTakeScreenshot?.Invoke(SnipMode.Freeform);
                break;
            case ID_SNIP_DELAYED:
                OnTakeScreenshot?.Invoke(SnipMode.Delayed);
                break;
            case ID_EXIT:
                OnExit?.Invoke();
                break;
        }
    }

    /// <summary>Restores and foregrounds a hidden top-level window.</summary>
    public static class TrayForeground
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static bool BringToFront(IntPtr hwnd)
        {
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                return SetForegroundWindow(hwnd);
            }
            catch { return false; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_added && _hwnd != IntPtr.Zero)
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                    szTip = string.Empty,
                };
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _added = false;
            }
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
            UnregisterClassW(_className, GetModuleHandleW(null));
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
