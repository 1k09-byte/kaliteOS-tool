using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Native;

namespace RestoreDefaults
{
    /// <summary>
    /// Returns every accessible process to stock scheduling state:
    /// priority Normal, EcoQoS OFF, priority boost enabled, full affinity,
    /// full default CPU Sets. Skips PID 0/4, Realtime (deliberate), and
    /// anything it cannot open (PPL/system). Exit 0 always (reports counts).
    /// </summary>
    internal static class Program
    {
        private static int _fixed, _clean, _skipped, _failed;

        private static int Main()
        {
            Console.WriteLine("=== RestoreDefaults sweep (REAL NativeMethods) ===");
            uint[] systemSets = ReadSystemCpuSetIds();
            Console.WriteLine($"system CPU sets: {systemSets.Length}");
            ulong fullMask = Environment.ProcessorCount >= 64
                ? ulong.MaxValue
                : ((1UL << Environment.ProcessorCount) - 1);

            foreach (var proc in Process.GetProcesses())
            {
                int pid;
                string name;
                try { pid = proc.Id; name = proc.ProcessName; }
                catch { continue; }
                finally { try { proc.Dispose(); } catch { } }

                if (pid <= 4) { _skipped++; continue; }

                try
                {
                    using var q = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
                    if (q.IsInvalid) { _skipped++; continue; }

                    uint prio = NativeMethods.Priority.GetPriorityClass(q);
                    if (prio == 0) { _skipped++; continue; }
                    if (prio == NativeMethods.Priority.Realtime) { _skipped++; continue; } // deliberate, don't touch

                    bool eco = false;
                    var st = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
                    if (NativeMethods.Power.GetProcessInformation(
                            q, ProcessInformationClass.ProcessPowerThrottling,
                            ref st, NativeMethods.Power.StateSize()))
                    {
                        eco = (st.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
                    }

                    bool boostDisabled = false;
                    bool boostRead = NativeMethods.Priority.GetProcessPriorityBoost(q, out bool dis);
                    if (boostRead) boostDisabled = dis;

                    bool needPrio = prio != NativeMethods.Priority.Normal;
                    bool needEco = eco;
                    bool needBoost = boostRead && boostDisabled;

                    bool needAffinity = false;
                    IntPtr curAffinity = IntPtr.Zero;
                    try
                    {
                        using var p2 = Process.GetProcessById(pid);
                        curAffinity = p2.ProcessorAffinity;
                        needAffinity = (ulong)curAffinity.ToInt64() != fullMask;
                    }
                    catch { }

                    bool needSets = false;
                    try
                    {
                        uint[]? cur = ReadProcessSets(q);
                        if (cur != null && cur.Length > 0 && cur.Length < systemSets.Length)
                            needSets = true;
                    }
                    catch { }

                    if (!needPrio && !needEco && !needBoost && !needAffinity && !needSets)
                    {
                        _clean++;
                        continue;
                    }

                    using var s = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
                    if (s.IsInvalid) { _failed++; continue; }

                    var changes = new List<string>();
                    if (needPrio && NativeMethods.Priority.SetPriorityClass(s, NativeMethods.Priority.Normal))
                        changes.Add($"prio {PrioName(prio)}->Normal");
                    if (needBoost && NativeMethods.Priority.SetProcessPriorityBoost(s, false))
                        changes.Add("boost on");
                    if (needEco)
                    {
                        var off = new ProcessPowerThrottlingState
                        {
                            Version = NativeMethods.Power.Version,
                            ControlMask = NativeMethods.Power.ExecutionSpeed,
                            StateMask = 0,
                        };
                        if (NativeMethods.Power.SetProcessInformation(
                                s, ProcessInformationClass.ProcessPowerThrottling,
                                ref off, NativeMethods.Power.StateSize()))
                            changes.Add("eco off");
                    }
                    if (needAffinity)
                    {
                        try
                        {
                            using var p3 = Process.GetProcessById(pid);
                            p3.ProcessorAffinity = (IntPtr)(long)fullMask;
                            changes.Add("affinity full");
                        }
                        catch { }
                    }
                    if (needSets && systemSets.Length > 0)
                    {
                        try
                        {
                            if (NativeMethods.CpuSets.SetProcessDefaultCpuSets(s, systemSets, (uint)systemSets.Length))
                                changes.Add("cpusets full");
                        }
                        catch { }
                    }

                    if (changes.Count > 0)
                    {
                        _fixed++;
                        Console.WriteLine($"  fixed {name} ({pid}): {string.Join(", ", changes)}");
                    }
                    else _failed++;
                }
                catch { _failed++; }
            }

            Console.WriteLine();
            Console.WriteLine($"fixed={_fixed} clean={_clean} skipped={_skipped} failed={_failed}");
            return 0;
        }

        private static string PrioName(uint p) => p switch
        {
            0x40 => "Idle",
            0x4000 => "BelowNormal",
            0x20 => "Normal",
            0x8000 => "AboveNormal",
            0x80 => "High",
            0x100 => "Realtime",
            _ => $"0x{p:X}",
        };

        private static uint[]? ReadProcessSets(SafeProcessHandle h)
        {
            try
            {
                if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, null, 0, out uint required))
                {
                    if (Marshal.GetLastWin32Error() != 122) return null;
                }
                if (required == 0) return Array.Empty<uint>();
                uint[] ids = new uint[required];
                if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, ids, required, out _)) return null;
                return ids;
            }
            catch { return null; }
        }

        private static uint[] ReadSystemCpuSetIds()
        {
            try
            {
                if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0))
                {
                    if (Marshal.GetLastWin32Error() != 122) return Array.Empty<uint>();
                }
                if (needed == 0 || needed > 64 * 1024 * 1024) return Array.Empty<uint>();
                byte[] buffer = new byte[needed];
                if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(buffer, needed, out _, IntPtr.Zero, 0))
                    return Array.Empty<uint>();
                var ids = new List<uint>();
                int offset = 0, guard = 0;
                while (offset + 20 <= buffer.Length && guard++ < 2048)
                {
                    uint size = BitConverter.ToUInt32(buffer, offset);
                    byte type = buffer[offset + 4];
                    if (size < 20 || offset + size > buffer.Length) break;
                    if (type == 0) ids.Add(BitConverter.ToUInt32(buffer, offset + 8));
                    offset += (int)size;
                }
                return ids.ToArray();
            }
            catch { return Array.Empty<uint>(); }
        }
    }
}
