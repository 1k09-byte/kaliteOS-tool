// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
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
