// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Management;
using System.Collections.Generic;

namespace kaliteConfig.Services;

public enum AffinityTargetType
{
    GPU,
    USB,
    WiFi
}

public static class AffinityOptimizerService
{
    private static int _logicalProcessors = -1;
    private static int _physicalCores = -1;

    public static void InitializeHardwareInfo()
    {
        if (_logicalProcessors != -1 && _physicalCores != -1) return;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var item in searcher.Get())
            {
                _physicalCores = Convert.ToInt32(item["NumberOfCores"]);
                _logicalProcessors = Convert.ToInt32(item["NumberOfLogicalProcessors"]);
                break; // Just grab the first proc cluster (assumes 1 socket for tuning scope)
            }
        }
        catch
        {
            // Fallback for permissions exhaustion
            _logicalProcessors = Environment.ProcessorCount;
            _physicalCores = Environment.ProcessorCount; // naive fallback assumes HT off
        }
    }

    public static bool IsHyperThreadingEnabled => _logicalProcessors > _physicalCores;
    
    public static int ActiveLogicalProcessors => _logicalProcessors;
    public static int ActivePhysicalCores => _physicalCores;

    /// <summary>
    /// Calculates the optimal bitmask based on target type, core count, and SMT availability.
    /// Follows the strict 6/8/8+ architecture separation heuristic.
    /// </summary>
    public static ulong CalculateOptimalMask(AffinityTargetType target)
    {
        InitializeHardwareInfo();
        ulong mask = 0;

        if (IsHyperThreadingEnabled)
        {
            // Threads per core = 2
            if (_physicalCores <= 6)
            {
                // 6 Cores (12 threads)
                // GPU -> Core 5 (threads 10, 11)
                // USB -> Core 4 (threads 8, 9)
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(5, 2);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(4, 2);
            }
            else if (_physicalCores == 8)
            {
                // 8 Cores (16 threads)
                // GPU -> Core 7
                // USB -> Core 6
                // WiFi -> Threads 4 and 5
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(7, 2);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(6, 2);
                else if (target == AffinityTargetType.WiFi) mask = (1UL << 4) | (1UL << 5);
            }
            else // > 8 cores
            {
                // > 8 Cores 
                // GPU -> Last 2 Cores
                // USB -> Preceeding 2 Cores
                // WiFi -> Threads 4 and 5
                int lastCore = _physicalCores - 1;
                if (target == AffinityTargetType.GPU) 
                    mask = BuildMaskFromCoreIndex(lastCore, 2) | BuildMaskFromCoreIndex(lastCore - 1, 2);
                else if (target == AffinityTargetType.USB) 
                    mask = BuildMaskFromCoreIndex(lastCore - 2, 2) | BuildMaskFromCoreIndex(lastCore - 3, 2);
                else if (target == AffinityTargetType.WiFi) 
                    mask = (1UL << 4) | (1UL << 5);
            }
        }
        else
        {
            // SMT Disabled (1 thread per core)
            if (_physicalCores <= 8)
            {
                // GPU -> Last core
                // USB -> Next-to-last core
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(_physicalCores - 1, 1);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(_physicalCores - 2, 1);
            }
            else // > 8 cores
            {
                // GPU -> Last 2 cores
                // USB -> Preceeding 2 cores
                int lastCore = _physicalCores - 1;
                if (target == AffinityTargetType.GPU) 
                    mask = BuildMaskFromCoreIndex(lastCore, 1) | BuildMaskFromCoreIndex(lastCore - 1, 1);
                else if (target == AffinityTargetType.USB) 
                    mask = BuildMaskFromCoreIndex(lastCore - 2, 1) | BuildMaskFromCoreIndex(lastCore - 3, 1);
            }
        }

        return mask;
    }

    /// <summary>
    /// Builds a bitmask for all threads hosted by the specified zero-indexed physical core.
    /// Core 0 generates 0b00000011 (3), Core 2 generates 0b00110000 (48)
    /// </summary>
    private static ulong BuildMaskFromCoreIndex(int coreIndex, int threadsPerCore)
    {
        ulong mask = 0;
        int startIndex = coreIndex * threadsPerCore;
        for (int i = 0; i < threadsPerCore; i++)
        {
            mask |= (1UL << (startIndex + i));
        }
        return mask;
    }
}
