using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services
{
    /// <summary>
    /// Detects physical core count and hybrid (P-core / E-core) topology via
    /// GetLogicalProcessorInformationEx.  Result is cached - hardware topology
    /// does not change at runtime.
    /// </summary>
    public static class TopologyService
    {
        private static CoreTopology? _cached;
        private static readonly object _lock = new();

        public static CoreTopology Get()
        {
            if (_cached is not null) return _cached;
            lock (_lock)
            {
                _cached ??= Detect();
            }
            return _cached;
        }

        // ── Win32 P/Invoke ──────────────────────────────────────────────────

        private const int RelationProcessorCore = 0;



        // Documented SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX header (variable-length).
        // https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-system_logical_processor_information_ex
        [StructLayout(LayoutKind.Sequential)]
        private struct SLPI_EX_HEADER
        {
            public int Relationship;  // LOGICAL_PROCESSOR_RELATIONSHIP enum
            public uint Size;         // Total size of this entry including trailing data
        }

        // PROCESSOR_RELATIONSHIP for RelationProcessorCore.
        // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship
        //
        // Layout (64-bit):
        //   BYTE  Flags           - 1 if SMT-capable core
        //   BYTE  EfficiencyClass - 0 = Performance, 1+ = Efficiency (non-hybrid CPUs: all 0)
        //   BYTE  Reserved[20]
        //   WORD  GroupCount
        //   GROUP_AFFINITY[ANYSIZE_ARRAY]
        //
        // We only need EfficiencyClass and the first GROUP_AFFINITY.Mask.
        //
        // GROUP_AFFINITY (16 bytes):
        //   KAFFINITY Mask (8 bytes on x64)
        //   WORD      Group
        //   WORD      Reserved[3]

        private const int SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER_SIZE = 8;
        
        private const int RelationNumaNode = 1;
        private const int RelationCache = 2;
        
        // RelationProcessorCore
        private const int EFFICIENCY_OFFSET      = 1;
        private const int CORE_GROUP_AFFINITY_OFFSET  = 24;
        
        // RelationNumaNode
        private const int NUMA_GROUP_AFFINITY_OFFSET = 24; // 4 (NodeNumber) + 20 (Reserved) = 24
        
        // RelationCache
        private const int CACHE_LEVEL_OFFSET     = 0;
        private const int CACHE_GROUP_AFFINITY_OFFSET = 40; // 32 (Fields) + 2 (GroupCount) + 6 (Pad) = 40

        private static CoreTopology Detect()
        {
            int logicalCount = Math.Max(1, Environment.ProcessorCount);

            var perfCoreMasks = new List<ulong>();
            var effCoreMasks = new List<ulong>();
            var numaNodeMasks = new HashSet<ulong>();
            var coreComplexMasks = new HashSet<ulong>();
            
            int physicalCoreCount = 0;

            try
            {
                uint len = 0;
                // Query ALL relationships (0xFFFF)
                Native.Kernel32.GetLogicalProcessorInformationEx(0xFFFF, IntPtr.Zero, ref len);
                if (len == 0) throw new InvalidOperationException("GetLogicalProcessorInformationEx returned 0 length");

                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!Native.Kernel32.GetLogicalProcessorInformationEx(0xFFFF, buf, ref len))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                    uint offset = 0;
                    while (offset < len)
                    {
                        IntPtr entryPtr = buf + (int)offset;
                        var header = Marshal.PtrToStructure<SLPI_EX_HEADER>(entryPtr);
                        IntPtr bodyPtr = entryPtr + SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER_SIZE;

                        if (header.Relationship == RelationProcessorCore)
                        {
                            physicalCoreCount++;

                            byte efficiencyClass = Marshal.ReadByte(bodyPtr, EFFICIENCY_OFFSET);
                            ulong mask = unchecked((ulong)Marshal.ReadInt64(bodyPtr + CORE_GROUP_AFFINITY_OFFSET));

                            if (efficiencyClass == 0)
                                perfCoreMasks.Add(mask);
                            else
                                effCoreMasks.Add(mask);
                        }
                        else if (header.Relationship == RelationNumaNode)
                        {
                            ulong mask = unchecked((ulong)Marshal.ReadInt64(bodyPtr + NUMA_GROUP_AFFINITY_OFFSET));
                            if (mask != 0) numaNodeMasks.Add(mask);
                        }
                        else if (header.Relationship == RelationCache)
                        {
                            byte level = Marshal.ReadByte(bodyPtr, CACHE_LEVEL_OFFSET);
                            // Process L3 Cache as Core Complex (CCX) boundaries
                            if (level == 3)
                            {
                                ulong mask = unchecked((ulong)Marshal.ReadInt64(bodyPtr + CACHE_GROUP_AFFINITY_OFFSET));
                                if (mask != 0) coreComplexMasks.Add(mask);
                            }
                        }

                        offset += header.Size;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TopologyService.Detect failed: {ex.Message}");
                // Graceful fallback: assume homogeneous SMT-2 cores.
                physicalCoreCount = Math.Max(1, logicalCount / 2);
                perfCoreMasks.Clear();
                effCoreMasks.Clear();
                numaNodeMasks.Clear();
                coreComplexMasks.Clear();
                int logicalIndex = 0;
                for (int i = 0; i < physicalCoreCount; i++)
                {
                    ulong mask = 0;
                    if (logicalIndex < logicalCount) mask |= (1UL << logicalIndex++);
                    if (logicalIndex < logicalCount) mask |= (1UL << logicalIndex++);
                    perfCoreMasks.Add(mask);
                }
                numaNodeMasks.Add((1UL << logicalCount) - 1);
                coreComplexMasks.Add((1UL << logicalCount) - 1);
            }

            // Remove reserved core 0 from the assignable performance list
            // (AllPerformanceCoreMasks keeps the full set for Optimize).
            var allPerfCoreMasks = perfCoreMasks.ToList();
            if (perfCoreMasks.Count > 0)
                perfCoreMasks.RemoveAt(0);

            return new CoreTopology
            {
                TotalLogicalCores = logicalCount,
                PhysicalCores = physicalCoreCount,
                ReservedCore = 0, // OS uses Core 0
                PerformanceCoreMasks = perfCoreMasks,
                AllPerformanceCoreMasks = allPerfCoreMasks,
                EfficiencyCoreMasks = effCoreMasks,
                NumaNodeMasks = numaNodeMasks.ToList(),
                CoreComplexMasks = coreComplexMasks.ToList()
            };
        }
    }
}
