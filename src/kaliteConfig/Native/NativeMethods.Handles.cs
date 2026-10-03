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

namespace kaliteConfig.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// Toolhelp32 process enumeration. Used to resolve every process's parent
    /// PID in one snapshot - the building block for the process tree.
    /// </summary>
    internal static partial class Toolhelp
    {
        internal const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        // PROCESSENTRY32 is a variable-length native struct (it ends in a
        // ByValTStr filename), which the source generator cannot marshal -
        // SYSLIB1051. These two stay on classic DllImport, which can.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    }

    internal static partial class Handles
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeProcessHandle OpenProcess(
            ProcessAccess desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeThreadHandle OpenThread(
            ThreadAccess desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint threadId);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentProcessId();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReadProcessMemory(
            SafeProcessHandle process,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            IntPtr size,
            out IntPtr bytesRead);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr LocalFree(IntPtr mem);
    }

    [Flags]
    internal enum ProcessAccess : uint
    {
        Terminate = 0x0001,
        SetInformation = 0x0200,
        QueryInformation = 0x0400,
        VirtualMemoryRead = 0x0010,
        SuspendResume = 0x0800,
        QueryLimitedInformation = 0x1000,
        Synchronize = 0x00100000,
    }

    [Flags]
    internal enum ThreadAccess : uint
    {
        Terminate = 0x0001,
        SuspendResume = 0x0002,
        GetContext = 0x0008,
        SetInformation = 0x0020,
        QueryInformation = 0x0040,
        QueryLimitedInformation = 0x0800,
    }

    internal static class Win32Error
    {
        internal const int Success = 0;
        internal const int AccessDenied = 5;
        internal const int InvalidHandle = 6;
        internal const int InvalidParameter = 87;
        internal const int PartialCopy = 299;
    }
}
