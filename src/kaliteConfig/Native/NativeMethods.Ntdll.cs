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

namespace kaliteConfig.Native;

internal static partial class NativeMethods
{
    internal static partial class Ntdll
    {
        internal const int SystemProcessInformation = 5;
        internal const int ThreadQuerySetWin32StartAddress = 9;
        internal const int ThreadNameInformation = 38;
        internal const int StatusBufferOverflow = unchecked((int)0x80000005);
        internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);
        internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        internal const int ProcessIoPriority = 33;

        [LibraryImport("ntdll.dll")]
        internal static partial int NtSetInformationProcess(
            SafeProcessHandle processHandle,
            int processInformationClass,
            ref uint processInformation,
            uint processInformationLength);

        // NTSTATUS return (0 = success). Only used for the documented
        // ProcessIoPriority query (class 33, ULONG out); every other use is
        // out of scope. Callers must treat nonzero as "unreadable", never fatal.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationProcess(
            SafeProcessHandle processHandle,
            int processInformationClass,
            ref uint processInformation,
            uint processInformationLength,
            out uint returnLength);

        // NTSTATUS return (0 = success). Undocumented/version-fragile: every caller
        // must validate sizes and degrade gracefully - see NativeSnapshotService.
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQuerySystemInformation(
            int systemInformationClass,
            [Out] byte[]? systemInformation,
            uint systemInformationLength,
            out uint returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationThread(
            SafeThreadHandle thread,
            int threadInformationClass,
            out IntPtr threadInformation,
            int threadInformationLength,
            out uint returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationThreadName(
            SafeThreadHandle thread,
            int threadInformationClass,
            IntPtr threadInformation,
            int threadInformationLength,
            out uint returnLength);
    }
}
