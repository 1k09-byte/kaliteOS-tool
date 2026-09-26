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
using System.Collections.Generic;

namespace kaliteConfig.Native;

/// <summary>
/// Reads every process's kernel+user CPU time in ONE syscall.
///
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> returns a linked
/// list of SYSTEM_PROCESS_INFORMATION records that already carry each process's
/// UserTime and KernelTime, so "who is using the CPU right now" costs one buffer
/// and zero process handles.
///
/// The handle-based alternative (Process.GetProcesses + OpenProcess +
/// GetProcessTimes per PID) costs a few hundred handle opens and closes per
/// sample. That is what the session contention monitor used to do once a second
/// for an entire game session - and the whole point of Game Mode is to cost less
/// than it saves.
///
/// Offsets are the 64-bit layout, matching <see cref="NativeSnapshotService"/>'s
/// parser. A 32-bit host lays these records out differently, so this refuses to
/// read at all rather than return garbage from the wrong offsets.
/// </summary>
internal static class SystemProcessCpuReader
{
    private const int MaxProcesses = 8192;
    private const int MaxBufferBytes = 256 * 1024 * 1024;

    // SYSTEM_PROCESS_INFORMATION, 64-bit:
    //   0x00 ULONG         NextEntryOffset
    //   0x04 ULONG         NumberOfThreads
    //   0x18 ULONGLONG     CycleTime
    //   0x28 LARGE_INTEGER UserTime      (100 ns units)
    //   0x30 LARGE_INTEGER KernelTime    (100 ns units)
    //   0x38 UNICODE_STRING ImageName
    //   0x50 HANDLE        UniqueProcessId
    private const int OffsetUserTime = 0x28;
    private const int OffsetKernelTime = 0x30;
    private const int OffsetProcessId = 0x50;

    /// <summary>Size of the fields read here, used as the "can I read this record" bound.</summary>
    private const int HeaderBytes = OffsetProcessId + 8;

    /// <summary>
    /// True when a sample was taken. <paramref name="cpuTicks"/> maps PID to
    /// kernel+user time in 100 ns ticks; diff two calls to get usage.
    /// </summary>
    internal static bool TryRead(out Dictionary<int, long> cpuTicks)
    {
        cpuTicks = new Dictionary<int, long>();
        if (IntPtr.Size != 8) return false;

        try
        {
            _ = NativeMethods.Ntdll.NtQuerySystemInformation(
                NativeMethods.Ntdll.SystemProcessInformation, null, 0, out uint needed);
            if (needed == 0 || needed > MaxBufferBytes) return false;

            byte[] buffer = new byte[needed + 1024];
            int status = NativeMethods.Ntdll.NtQuerySystemInformation(
                NativeMethods.Ntdll.SystemProcessInformation, buffer, (uint)buffer.Length, out _);
            if (status != 0) return false;

            int offset = 0;
            int guard = 0;
            while (offset >= 0 && offset + HeaderBytes <= buffer.Length && guard++ < MaxProcesses)
            {
                int next = BitConverter.ToInt32(buffer, offset);
                int pid = BitConverter.ToInt32(buffer, offset + OffsetProcessId);
                if (pid > 0)
                {
                    long user = BitConverter.ToInt64(buffer, offset + OffsetUserTime);
                    long kernel = BitConverter.ToInt64(buffer, offset + OffsetKernelTime);
                    cpuTicks[pid] = user + kernel;
                }

                // Zero marks the last record. A negative/non-advancing offset
                // would loop forever, so stop on anything that is not progress.
                if (next <= 0) break;
                offset += next;
            }

            return cpuTicks.Count > 0;
        }
        catch
        {
            cpuTicks.Clear();
            return false;
        }
    }
}
