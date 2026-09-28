using System;
using System.Management;

public enum AffinityTargetType { GPU, USB, WiFi }

class Program
{
    private static int _logicalProcessors = -1;
    private static int _physicalCores = -1;

    public static void InitializeHardwareInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var item in searcher.Get())
            {
                _physicalCores = Convert.ToInt32(item["NumberOfCores"]);
                _logicalProcessors = Convert.ToInt32(item["NumberOfLogicalProcessors"]);
                break;
            }
        }
        catch
        {
            _logicalProcessors = Environment.ProcessorCount;
            _physicalCores = Environment.ProcessorCount;
        }
        Console.WriteLine($"Cores: {_physicalCores}, Logical: {_logicalProcessors}");
    }

    public static ulong CalculateOptimalMask(AffinityTargetType target)
    {
        InitializeHardwareInfo();
        ulong mask = 0;
        bool ht = _logicalProcessors > _physicalCores;
        
        if (ht) {
            if (_physicalCores <= 6) {
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(5, 2);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(4, 2);
            }
            else if (_physicalCores == 8) {
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(7, 2);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(6, 2);
                else if (target == AffinityTargetType.WiFi) mask = (1UL << 4) | (1UL << 5);
            }
            else {
                int lastCore = _physicalCores - 1;
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(lastCore, 2) | BuildMaskFromCoreIndex(lastCore - 1, 2);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(lastCore - 2, 2) | BuildMaskFromCoreIndex(lastCore - 3, 2);
                else if (target == AffinityTargetType.WiFi) mask = (1UL << 4) | (1UL << 5);
            }
        } else {
            if (_physicalCores <= 8) {
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(_physicalCores - 1, 1);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(_physicalCores - 2, 1);
            }
            else {
                int lastCore = _physicalCores - 1;
                if (target == AffinityTargetType.GPU) mask = BuildMaskFromCoreIndex(lastCore, 1) | BuildMaskFromCoreIndex(lastCore - 1, 1);
                else if (target == AffinityTargetType.USB) mask = BuildMaskFromCoreIndex(lastCore - 2, 1) | BuildMaskFromCoreIndex(lastCore - 3, 1);
            }
        }
        return mask;
    }

    private static ulong BuildMaskFromCoreIndex(int coreIndex, int threadsPerCore)
    {
        ulong mask = 0;
        int startIndex = coreIndex * threadsPerCore;
        for (int i = 0; i < threadsPerCore; i++) mask |= (1UL << (startIndex + i));
        return mask;
    }

    static void Main() {
        Console.WriteLine("GPU: " + CalculateOptimalMask(AffinityTargetType.GPU));
        Console.WriteLine("USB: " + CalculateOptimalMask(AffinityTargetType.USB));
        Console.WriteLine("WiFi: " + CalculateOptimalMask(AffinityTargetType.WiFi));
    }
}
