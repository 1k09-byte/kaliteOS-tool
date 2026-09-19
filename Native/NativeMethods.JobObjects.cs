using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kaliteConfig.Native;

internal static partial class NativeMethods
{
    internal static partial class JobObjects
    {
        public const int JobObjectCpuRateControlInformation = 15;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_WEIGHT_BASED = 0x2;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        public static partial SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(
            SafeFileHandle hJob,
            int JobObjectInfoClass,
            ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInfo,
            int cbJobObjectInfoLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(
            SafeFileHandle hJob,
            SafeProcessHandle hProcess);
    }
}
