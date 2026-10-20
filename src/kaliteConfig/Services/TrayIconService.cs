using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

public sealed class TrayIconService : IDisposable
{
    public Action? OnOpen;
    public Action? OnExit;
    public Action? OnPauseLive;
    public Action? OnStopLive;
    public Action? OnResumeGame;
    public Func<(bool CanPause, bool CanStop)>? LiveStateQuery;
    public Func<bool>? GameModeHeldQuery;
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
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint ID_VERSION = 99;
    private const uint ID_OPEN = 100;
    private const uint ID_TAKE_SNIP = 101;
    private const uint ID_PAUSE_LIVE = 102;
    private const uint ID_STOP_LIVE = 103;
    private const uint ID_RESUME_GAME = 104;
    private const uint ID_SEPARATOR1 = 107;
    private const uint ID_EXIT = 108;

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

    private GCHandle _procPin;

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

            if (!_procPin.IsAllocated) _procPin = GCHandle.Alloc(_proc);

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
                if (wParam != (IntPtr)1) return DefWindowProcW(hWnd, msg, wParam, lParam);

                uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                if (System.Threading.Interlocked.CompareExchange(ref _inCallback, 1, 0) == 0)
                {
                    try
                    {
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
            AppendMenuW(menu, MF_STRING | MF_GRAYED, ID_VERSION, "kaliteConfig v" + AppVersion);
            AppendMenuW(menu, MF_SEPARATOR, ID_SEPARATOR1, null);

            AppendMenuW(menu, MF_STRING, ID_OPEN, "Open kaliteConfig");

            AppendMenuW(menu, MF_STRING, ID_TAKE_SNIP, "Take Screenshot");

            bool canPause = false;
            bool canStop = false;
            try
            {
                if (LiveStateQuery is { } query) (canPause, canStop) = query();
            }
            catch { }
            AppendMenuW(menu, canPause ? MF_STRING : MF_STRING | MF_GRAYED, ID_PAUSE_LIVE, "Pause live wallpaper");
            AppendMenuW(menu, canStop ? MF_STRING : MF_STRING | MF_GRAYED, ID_STOP_LIVE, "Stop live wallpaper");

            bool gameModeHeld = false;
            try
            {
                if (GameModeHeldQuery is { } held) gameModeHeld = held();
            }
            catch { }
            AppendMenuW(menu, gameModeHeld ? MF_STRING : MF_STRING | MF_GRAYED, ID_RESUME_GAME, "Resume Game Mode changes");

            AppendMenuW(menu, MF_SEPARATOR, ID_SEPARATOR1, null);

            AppendMenuW(menu, MF_STRING, ID_EXIT, "Exit");

            if (!GetCursorPos(out POINT pt)) { Tlog("GetCursorPos failed"); return; }
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
            uint myThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try
            {
                SetForegroundWindow(_hwnd);
                uint picked = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd);
                PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Tlog($"picked={picked} attached={attached}");
                HandleMenuSelection(picked);
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
            case ID_TAKE_SNIP:
                OnTakeScreenshot?.Invoke(SnipMode.Region);
                break;
            case ID_PAUSE_LIVE:
                OnPauseLive?.Invoke();
                break;
            case ID_STOP_LIVE:
                OnStopLive?.Invoke();
                break;
            case ID_RESUME_GAME:
                OnResumeGame?.Invoke();
                break;
            case ID_EXIT:
                OnExit?.Invoke();
                break;
        }
    }

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

            if (_procPin.IsAllocated) { _procPin.Free(); _procPin = default; }
            _proc = null;

            UnregisterClassW(_className, GetModuleHandleW(null));
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
