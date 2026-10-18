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

internal enum CpuSetInformationType : byte
{
    CpuSetInformation = 0,
}

internal static partial class NativeMethods
{
    internal static partial class CpuSets
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetSystemCpuSetInformation(
            [Out] byte[]? information,
            uint bufferLength,
            out uint returnedLength,
            IntPtr process,
            uint flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessDefaultCpuSets(
            SafeProcessHandle process,
            [Out] uint[]? cpuSetIds,
            uint cpuSetIdsLength,
            out uint requiredLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessDefaultCpuSets(
            SafeProcessHandle process,
            [In] uint[] cpuSetIds,
            uint cpuSetIdsCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetThreadSelectedCpuSets(
            SafeThreadHandle thread,
            [Out] uint[]? cpuSetIds,
            uint cpuSetIdsLength,
            out uint requiredLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetThreadSelectedCpuSets(
            SafeThreadHandle thread,
            [In] uint[] cpuSetIds,
            uint cpuSetIdsCount);
    }
}

public sealed class CpuSetEntry
{
    internal uint Id { get; init; }
    internal ushort Group { get; init; }
    internal byte LogicalIndex { get; init; }
    internal byte CoreIndex { get; init; }
    internal byte LastLevelCacheIndex { get; init; }
    internal byte NumaNodeIndex { get; init; }
    internal byte EfficiencyClass { get; init; }
    internal bool Parked { get; init; }
    internal bool Allocated { get; init; }
}
