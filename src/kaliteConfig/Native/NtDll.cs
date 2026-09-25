using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native
{
    public static class NtDll
    {
        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(
            IntPtr ProcessHandle, 
            int ProcessInformationClass, 
            ref PS_PROTECTION ProcessInformation, 
            int ProcessInformationLength, 
            out int ReturnLength);
            
        [StructLayout(LayoutKind.Sequential)]
        public struct PS_PROTECTION
        {
            public byte Level;
            public byte Type;
            public byte Signer;
            public byte Audit;
        }
    }
}
