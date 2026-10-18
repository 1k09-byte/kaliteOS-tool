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

internal enum ProcessInformationClass : uint
{
    ProcessMemoryPriority = 0,
    ProcessPowerThrottling = 4,
}

internal enum ThreadInformationClass : uint
{
    ThreadMemoryPriority = 0,
    ThreadPowerThrottling = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerThrottlingState
{
    internal uint Version;
    internal uint ControlMask;
    internal uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryPriorityInfo
{
    internal uint MemoryPriority;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ThreadPowerThrottlingState
{
    internal uint Version;
    internal uint ControlMask;
    internal uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ThreadMemoryPriorityInfo
{
    internal uint MemoryPriority;
}

internal static partial class NativeMethods
{
    internal static partial class Power
    {
        internal const uint Version = 1;
        internal const uint ExecutionSpeed = 0x4;

        internal const uint ThreadVersion = 1;
        internal const uint ThreadExecutionSpeed = 0x1;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessMemoryPriorityInfo info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessInformation(
            SafeProcessHandle process,
            ProcessInformationClass infoClass,
            ref ProcessMemoryPriorityInfo info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadMemoryPriorityInfo info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadPowerThrottlingState info,
            uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadInformation(
            SafeThreadHandle thread,
            ThreadInformationClass infoClass,
            ref ThreadMemoryPriorityInfo info,
            uint size);

        internal static uint StateSize() => (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
        internal static uint MemoryPrioritySize() => (uint)Marshal.SizeOf<ProcessMemoryPriorityInfo>();

        internal static uint ThreadStateSize() => (uint)Marshal.SizeOf<ThreadPowerThrottlingState>();
    }
}
