using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace stellarisKIT.Native
{
    public static class Advapi32
    {
        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ServiceControlManagerSafeHandle OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern ServiceSafeHandle OpenService(ServiceControlManagerSafeHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool ChangeServiceConfig(
            ServiceSafeHandle hService,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string? lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword,
            string? lpDisplayName);

        public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        public const uint SERVICE_ALL_ACCESS = 0xF01FF;
        public const uint SERVICE_CHANGE_CONFIG = 0x0002;
        
        public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        public const uint SERVICE_BOOT_START = 0x00000000;
        public const uint SERVICE_SYSTEM_START = 0x00000001;
        public const uint SERVICE_AUTO_START = 0x00000002;
        public const uint SERVICE_DEMAND_START = 0x00000003;
        public const uint SERVICE_DISABLED = 0x00000004;
    }

    public class ServiceControlManagerSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceControlManagerSafeHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);
    }

    public class ServiceSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceSafeHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);
    }
}
