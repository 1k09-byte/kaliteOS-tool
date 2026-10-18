using System;

namespace kaliteConfig.Services.Calculators;

public static class CruTimingCalculators
{
    private const long MinTimeCVT = 550000000;
    private const long MinTimeCVTRB = 460000000;
    private const long MinTimeCVTRB2 = 460000000;
    
    private const int C = 40;
    private const int J = 20;
    private const int K = 128;
    private const int M = 600;
    
    private const long CPrime = 30;
    private const long MPrime = 300;

    public struct TimingParameters
    {
        public bool IsValid;
        public int PixelClock10kHz;
        
        public int HActive;
        public int HFrontPorch;
        public int HSyncWidth;
        public int HBackPorch;
        public int HBlank;
        public int HTotal;
        
        public int VActive;
        public int VFrontPorch;
        public int VSyncWidth;
        public int VBackPorch;
        public int VBlank;
        public int VTotal;
        
        public bool HSyncPositive;
        public bool VSyncPositive;
        public bool Interlaced;
        
        public double ActualVRate;
        public double ActualHRate;
    }

    private static int GetVFrontForCVT(int fields = 1) => 3 * fields;
    
    private static int GetVSyncForCVT(int hActive, int vActive, int fields = 1)
    {
        int aspect = vActive * 4000 / hActive;
        if (aspect >= 2205 && aspect <= 2295) return 5 * fields;
        if (aspect >= 2352 && aspect <= 2448) return 7 * fields;
        if (aspect >= 2450 && aspect <= 2550) return 6 * fields;
        if (aspect >= 2940 && aspect <= 3060) return 4 * fields;
        if (aspect >= 3136 && aspect <= 3264) return 7 * fields;
        return 10 * fields;
    }

    public static TimingParameters CalculateCVTStandard(int hActive, int vActive, double vRateHz, bool interlaced)
    {
        long vRate = (long)Math.Round(vRateHz * 1000.0);
        int fields = interlaced ? 2 : 1;
        int interlacedInt = interlaced ? 1 : 0;

        int vFront = GetVFrontForCVT(fields);
        int vSync = GetVSyncForCVT(hActive, vActive, fields);

        long hPeriod = (1000000000000000L * fields / vRate - MinTimeCVT * fields) / (vActive + vFront + interlacedInt);
        
        int vSyncVBack = (int)(MinTimeCVT / hPeriod + 1);
        int vBack = vSyncVBack * fields + interlacedInt - vSync;
        if (vBack < 6 * fields + interlacedInt) vBack = 6 * fields + interlacedInt;
        
        int vBlank = vFront + vSync + vBack;
        int vTotal = vActive + vBlank;

        long idealDutyCycle = CPrime * 1000000000L - MPrime * hPeriod;
        if (idealDutyCycle < 20000000000L) idealDutyCycle = 20000000000L;
        
        int hBlank = (int)(hActive * idealDutyCycle / (100000000000L - idealDutyCycle) / 16 * 16);
        int hSync = (hActive + hBlank) / 100 * 8;
        int hBack = hBlank / 2;
        int hFront = hBlank - hSync - hBack;
        int hTotal = hActive + hBlank;

        long multiplier = 10000000000L;
        int multiple = 2500;
        long actualPClock = hTotal * multiplier / hPeriod / multiple * multiple;
        
        double actualVRate = (double)actualPClock * 1000.0 / hTotal / vTotal;

        return new TimingParameters
        {
            IsValid = true,
            PixelClock10kHz = (int)(actualPClock / 10000),
            HActive = hActive,
            HFrontPorch = hFront,
            HSyncWidth = hSync,
            HBackPorch = hBack,
            HBlank = hBlank,
            HTotal = hTotal,
            VActive = vActive,
            VFrontPorch = vFront,
            VSyncWidth = vSync,
            VBackPorch = vBack,
            VBlank = vBlank,
            VTotal = vTotal,
            HSyncPositive = false,
            VSyncPositive = true,
            Interlaced = interlaced,
            ActualVRate = ((double)actualPClock / 10000.0) * 10000.0 / hTotal / vTotal,
            ActualHRate = ((double)actualPClock / 10000.0) * 10000.0 / hTotal / 1000.0
        };
    }
    
    public static TimingParameters CalculateCVTRBStandard(int hActive, int vActive, double vRateHz, bool interlaced)
    {
        long vRate = (long)Math.Round(vRateHz * 1000.0);
        int fields = interlaced ? 2 : 1;
        int interlacedInt = interlaced ? 1 : 0;
        
        int hFront = 48;
        int hSync = 32;
        int hBack = 80;
        int hBlank = hFront + hSync + hBack;
        int hTotal = hActive + hBlank;

        long hPeriod = (1000000000000000L * fields / vRate - MinTimeCVTRB * fields) / vActive;
        
        int vFront = GetVFrontForCVT(fields);
        int vSync = GetVSyncForCVT(hActive, vActive, fields);
        
        int minVBlank = (int)(MinTimeCVTRB / hPeriod + 1);
        int vBack = minVBlank * fields + interlacedInt - vFront - vSync;
        if (vBack < 6 * fields + interlacedInt) vBack = 6 * fields + interlacedInt;
        
        int vBlank = vFront + vSync + vBack;
        int vTotal = vActive + vBlank;

        int multiple = 2500;
        long actualPClock = vRate * hTotal * vTotal / 1000000L / multiple * multiple;

        return new TimingParameters
        {
            IsValid = true,
            PixelClock10kHz = (int)(actualPClock / 10000), 
            HActive = hActive,
            HFrontPorch = hFront,
            HSyncWidth = hSync,
            HBackPorch = hBack,
            HBlank = hBlank,
            HTotal = hTotal,
            VActive = vActive,
            VFrontPorch = vFront,
            VSyncWidth = vSync,
            VBackPorch = vBack,
            VBlank = vBlank,
            VTotal = vTotal,
            HSyncPositive = true,
            VSyncPositive = false,
            Interlaced = interlaced,
            ActualVRate = ((double)actualPClock / 10000.0) * 10000.0 / hTotal / vTotal,
            ActualHRate = ((double)actualPClock / 10000.0) * 10000.0 / hTotal / 1000.0
        };
    }
}
