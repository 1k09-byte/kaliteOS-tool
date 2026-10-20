using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

internal static partial class NativeMethods
{
    internal static class GameMode
    {
        internal const uint EventSystemForeground = 0x0003;
        internal const uint WineventOutofcontext = 0x0000;
        internal const uint WineventSkipownprocess = 0x0002;
        internal const int ProcessPowerThrottling = 4;
        internal const uint PowerThrottlingExecutionSpeed = 0x1;
        internal const int ProcessProtectionLevelInfo = 7;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            internal IntPtr Hwnd;
            internal uint Message;
            internal IntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int PointX;
            internal int PointY;
            internal uint Private;
        }

        [DllImport("user32.dll")]
        internal static extern int GetMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref Msg message);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        internal static readonly IntPtr HwndMessage = new(-3);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PowerThrottlingState
        {
            internal uint Version;
            internal uint ControlMask;
            internal uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProtectionLevelInfo
        {
            internal uint ProtectionLevel;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetProcessInformation(IntPtr process, int infoClass, ref ProtectionLevelInfo info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool IsProcessCritical(IntPtr process, out bool critical);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr mem);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool EnumServicesStatusEx(
            IntPtr manager,
            int level,
            uint serviceType,
            uint serviceState,
            IntPtr buffer,
            int bufferSize,
            out int bytesNeeded,
            out int servicesReturned,
            ref int resumeHandle,
            string? groupName);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(IntPtr process);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(IntPtr process);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetPriorityClass(IntPtr process, out uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        internal const uint BelowNormalPriorityClass = 0x00004000;
        internal const uint NormalPriorityClass = 0x00000020;

        internal const int ScStatusProcessInfo = 0;
        internal const uint ScEnumProcessInfo = 0x00000003;
        internal const uint ScManagerEnumerateService = 0x0004;
        internal const uint ServiceWin32 = 0x00000030;
        internal const uint ServiceStateAll = 0x00000003;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EnumServiceStatusProcess
        {
            internal IntPtr ServiceName;
            internal IntPtr DisplayName;
            internal ServiceStatusProcess Status;
        }
    }
}
