// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace kaliteConfig.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorNumber
{
    internal ushort Group;
    internal byte Number;
    internal byte Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GroupAffinity
{
    internal ulong Mask;
    internal ushort Group;
    internal ushort Reserved0;
    internal ushort Reserved1;
    internal ushort Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    internal uint LowDateTime;
    internal uint HighDateTime;

    internal readonly long ToTicks() => ((long)HighDateTime << 32) | LowDateTime;
}

internal static partial class NativeMethods
{
    // SetupDi device-property read - the same mechanism the reference tool
    // uses for DEVPKEY_PciDevice_InterruptMessageMaximum.
    internal static partial class SetupApi
    {
        internal const uint DIGCF_PRESENT = 0x00000002;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            internal uint cbSize;
            internal Guid ClassGuid;
            internal uint DevInst;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVPROPKEY
        {
            internal Guid fmtid;
            internal uint pid;
        }

        // pciprop.h: DEFINE_PCI_DEVICE_DEVPKEY(DEVPKEY_PciDevice_InterruptMessageMaximum, 15).
        // GUID verified against the live property store via SetupDiGetDevicePropertyKeys.
        internal static readonly DEVPROPKEY DEVPKEY_PciDevice_InterruptMessageMaximum = new()
        {
            fmtid = new Guid("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62"),
            pid = 15
        };

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        // NOTE: explicit W entry point - this API is Unicode-only; without it
        // the runtime probes for a nonexistent ...PropertyA and every call throws.
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey, out uint propertyType, [Out] byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }

    internal static partial class Affinity
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial ushort GetActiveProcessorCount(ushort groupNumber);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial ushort GetActiveProcessorGroupCount();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessAffinityMask(
            SafeProcessHandle process,
            out IntPtr processAffinityMask,
            out IntPtr systemAffinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessAffinityMask(SafeProcessHandle process, IntPtr affinityMask);

        // Returns the previous mask; 0 (IntPtr.Zero) means failure.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial IntPtr SetThreadAffinityMask(SafeThreadHandle thread, IntPtr affinityMask);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadGroupAffinity(
            SafeThreadHandle thread,
            out GroupAffinity groupAffinity);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadGroupAffinity(
            SafeThreadHandle thread,
            ref GroupAffinity groupAffinity,
            IntPtr previousGroupAffinity);

        // GetThreadIdealProcessorEx takes only (thread, out ideal) - no third
        // parameter - and returns BOOL, filling the PPROCESSOR_NUMBER out
        // parameter. Measured against kernel32 itself (tools/ideal-processor-probe.ps1:
        // returns 1/TRUE while the processor number comes back in the struct).
        //
        // This used to be declared as returning the processor number, with
        // "(DWORD)-1 on failure": that comparison never matched, so the failure
        // branch was dead code and a failed read reported an uninitialised
        // processor as if it were the thread's live ideal processor.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadIdealProcessorEx(
            SafeThreadHandle thread,
            out ProcessorNumber currentIdeal);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadIdealProcessorEx(
            SafeThreadHandle thread,
            ref ProcessorNumber ideal,
            IntPtr previousIdeal);
    }

    internal static partial class Times
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(
            SafeProcessHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadTimes(
            SafeThreadHandle thread,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int QueryThreadCycleTime(SafeThreadHandle thread, out ulong cycleTime);
    }

    internal static partial class Threads
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint SuspendThread(SafeThreadHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint ResumeThread(SafeThreadHandle thread);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateThread(SafeThreadHandle thread, uint exitCode);

        // Returns HRESULT (0 = S_OK). Frees with LocalFree - never leak the pointer.
        [LibraryImport("kernel32.dll")]
        internal static partial int GetThreadDescription(SafeThreadHandle thread, out IntPtr description);

        [LibraryImport("kernel32.dll")]
        internal static partial int SetThreadDescription(SafeThreadHandle thread, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }
}
