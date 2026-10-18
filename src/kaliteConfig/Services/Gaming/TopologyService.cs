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
using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services
{
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

        private const int RelationProcessorCore = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SLPI_EX_HEADER
        {
            public int Relationship;
            public uint Size;
        }

        private const int SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX_HEADER_SIZE = 8;
        
        private const int RelationNumaNode = 1;
        private const int RelationCache = 2;

        private const int EFFICIENCY_OFFSET      = 1;
        private const int CORE_GROUP_AFFINITY_OFFSET  = 24;

        private const int NUMA_GROUP_AFFINITY_OFFSET = 24;

        private const int CACHE_LEVEL_OFFSET     = 0;
        private const int CACHE_GROUP_AFFINITY_OFFSET = 40;

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
            catch (Exception)
            {
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

            var allPerfCoreMasks = perfCoreMasks.ToList();
            if (perfCoreMasks.Count > 0)
                perfCoreMasks.RemoveAt(0);

            return new CoreTopology
            {
                TotalLogicalCores = logicalCount,
                PhysicalCores = physicalCoreCount,
                ReservedCore = 0,
                PerformanceCoreMasks = perfCoreMasks,
                AllPerformanceCoreMasks = allPerfCoreMasks,
                EfficiencyCoreMasks = effCoreMasks,
                NumaNodeMasks = numaNodeMasks.ToList(),
                CoreComplexMasks = coreComplexMasks.ToList()
            };
        }
    }
}
