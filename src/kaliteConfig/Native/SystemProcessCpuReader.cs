using System;
using System.Collections.Generic;

namespace kaliteConfig.Native;

internal static class SystemProcessCpuReader
{
    private const int MaxProcesses = 8192;
    private const int MaxBufferBytes = 256 * 1024 * 1024;

    private const int OffsetUserTime = 0x28;
    private const int OffsetKernelTime = 0x30;
    private const int OffsetProcessId = 0x50;

    private const int HeaderBytes = OffsetProcessId + 8;

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
