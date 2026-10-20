using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using kaliteConfig.Native;

namespace kaliteConfig.Gaming.GameMode;

public sealed class ForegroundWatcher : IDisposable
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private IntPtr _window;
    private volatile bool _running;
    private NativeMethods.GameMode.WinEventProc? _callback;

    public event Action<IntPtr>? ForegroundChanged;
    public event Action<string>? Diagnostic;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "GameModeForeground" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_threadId != 0)
            NativeMethods.GameMode.PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        try { _thread?.Join(2000); } catch { }
        _thread = null;
    }

    private void Loop()
    {
        _threadId = NativeMethods.GameMode.GetCurrentThreadId();
        _window = NativeMethods.GameMode.CreateWindowEx(0, "STATIC", "kaliteConfigGameMode", 0,
            0, 0, 0, 0, NativeMethods.GameMode.HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.GameMode.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        _callback = OnWinEvent;
        _hook = NativeMethods.GameMode.SetWinEventHook(
            NativeMethods.GameMode.EventSystemForeground,
            NativeMethods.GameMode.EventSystemForeground,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.GameMode.WineventOutofcontext | NativeMethods.GameMode.WineventSkipownprocess);

        try
        {
            Diagnostic?.Invoke(_hook == IntPtr.Zero
                ? "Foreground hook was not installed"
                : "Foreground hook installed (window " + _window + ")");
        }
        catch { }

        while (_running)
        {
            int result = NativeMethods.GameMode.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result == 0 || result == -1) break;
            NativeMethods.GameMode.TranslateMessage(ref message);
            NativeMethods.GameMode.DispatchMessage(ref message);
        }

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.GameMode.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_window != IntPtr.Zero)
        {
            NativeMethods.GameMode.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        try { Diagnostic?.Invoke("Foreground message loop exited"); } catch { }
        _threadId = 0;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType != NativeMethods.GameMode.EventSystemForeground || hwnd == IntPtr.Zero) return;
        try { ForegroundChanged?.Invoke(hwnd); } catch { }
    }

    public static bool TryResolveForeground(IntPtr hwnd, out int pid, out string processName, out string? imagePath)
    {
        pid = 0;
        processName = string.Empty;
        imagePath = null;
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == NativeMethods.GameMode.GetShellWindow()) return false;

            var builder = new StringBuilder(256);
            NativeMethods.GameMode.GetClassName(hwnd, builder, builder.Capacity);
            string windowClass = builder.ToString();
            foreach (var ignored in BackgroundCatalog.IgnoredForegroundClasses)
                if (windowClass.Equals(ignored, StringComparison.OrdinalIgnoreCase)) return false;

            NativeMethods.GameMode.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == 0) return false;

            builder.Clear();
            NativeMethods.GameMode.GetWindowText(hwnd, builder, builder.Capacity);
            string title = builder.ToString();
            if (string.IsNullOrWhiteSpace(title)) return false;

            IntPtr target = hwnd;
            if (windowClass.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr childHolder = IntPtr.Zero;
                NativeMethods.GameMode.EnumChildWindows(hwnd, (child, _) =>
                {
                    NativeMethods.GameMode.GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid != 0 && childPid != owner)
                    {
                        childHolder = child;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (childHolder != IntPtr.Zero) target = childHolder;
            }

            NativeMethods.GameMode.GetWindowThreadProcessId(target, out uint resolved);
            if (resolved == 0) return false;
            pid = (int)resolved;

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            processName = process.ProcessName + ".exe";
            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, resolved);
            if (handle.IsInvalid) return true;
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            if (NativeMethods.GameMode.QueryFullProcessImageName(handle.DangerousGetHandle(), 0, path, ref size))
                imagePath = path.ToString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
