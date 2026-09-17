using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

public sealed class ProtectedProcessService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PS_PROTECTION
    {
        public byte Level;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        ref PS_PROTECTION ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessProtectionInformation = 61; // ProcessProtectionInformation class

    public bool IsProcessProtected(int pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // If we can't even get limited query access, it's either dead or highly protected (e.g., heavily shielded anti-cheat or system).
            return true;
        }

        try
        {
            PS_PROTECTION protection = new PS_PROTECTION();
            int returnLength;
            int status = NtQueryInformationProcess(
                hProcess,
                ProcessProtectionInformation,
                ref protection,
                Marshal.SizeOf(typeof(PS_PROTECTION)),
                out returnLength);

            if (status == 0) // STATUS_SUCCESS
            {
                return protection.Level > 0;
            }
            
            return false;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}
