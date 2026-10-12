// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Management;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

internal static class EdidEngineService
{
    /// <summary>
    /// Reads the raw hardware EDID using WMI and converts it to a mutable EdidProfile.
    /// Requires the WMI instance name (e.g. DISPLAY\XEC2451\5&f287462&3&UID4355_0).
    /// </summary>
    public static EdidProfile? ReadHardwareProfile(string instanceName)
    {
        try
        {
            string escapedInstance = instanceName.Replace("\\", "\\\\");
            using var classObj = new ManagementObject(
                $@"root\wmi:WmiMonitorDescriptorMethods.InstanceName='{escapedInstance}'");

            var profile = new EdidProfile();
            
            // Read Block 0 (Base EDID)
            var inParams0 = classObj.GetMethodParameters("WmiGetMonitorRawEEdidV1Block");
            inParams0["BlockId"] = 0;
            var outParams0 = classObj.InvokeMethod("WmiGetMonitorRawEEdidV1Block", inParams0, null);
            
            if (outParams0?["BlockContent"] is byte[] baseBlock && baseBlock.Length >= 128)
            {
                profile.Blocks.Add(new EdidBlock(baseBlock, false));
                
                // Check extension block count
                int extCount = baseBlock[126];
                for (byte i = 1; i <= extCount; i++)
                {
                    var inParamsExt = classObj.GetMethodParameters("WmiGetMonitorRawEEdidV1Block");
                    inParamsExt["BlockId"] = i;
                    var outParamsExt = classObj.InvokeMethod("WmiGetMonitorRawEEdidV1Block", inParamsExt, null);
                    if (outParamsExt?["BlockContent"] is byte[] extBlock && extBlock.Length >= 128)
                    {
                        profile.Blocks.Add(new EdidBlock(extBlock, true));
                    }
                }
                return profile;
            }
        }
        catch
        {
            // Fallback gracefully on parsing failure
        }
        return null;
    }

    /// <summary>
    /// Generates an 18-byte Detailed Timing Descriptor for a specified resolution and timing parameters.
    /// </summary>
    public static byte[] GenerateDetailedTimingDescriptor(
        int pixelClockKHz,
        int hActive, int hBlanking, int hFrontPorch, int hSyncWidth,
        int vActive, int vBlanking, int vFrontPorch, int vSyncWidth,
        bool hSyncPositive, bool vSyncPositive, bool interlaced)
    {
        byte[] dtd = new byte[18];
        
        // Pixel clock (bytes 0-1) is in 10 kHz units
        int pclk = pixelClockKHz / 10;
        dtd[0] = (byte)(pclk & 0xFF);
        dtd[1] = (byte)((pclk >> 8) & 0xFF);

        // H Active / H Blanking (bytes 2-4)
        dtd[2] = (byte)(hActive & 0xFF);
        dtd[3] = (byte)(hBlanking & 0xFF);
        dtd[4] = (byte)(((hActive >> 8) & 0x0F) << 4 | ((hBlanking >> 8) & 0x0F));

        // V Active / V Blanking (bytes 5-7)
        dtd[5] = (byte)(vActive & 0xFF);
        dtd[6] = (byte)(vBlanking & 0xFF);
        dtd[7] = (byte)(((vActive >> 8) & 0x0F) << 4 | ((vBlanking >> 8) & 0x0F));

        // H/V Sync Offset and Width (bytes 8-11)
        dtd[8] = (byte)(hFrontPorch & 0xFF);
        dtd[9] = (byte)(hSyncWidth & 0xFF);
        dtd[10] = (byte)(((vFrontPorch & 0x0F) << 4) | (vSyncWidth & 0x0F));
        dtd[11] = (byte)(
            ((hFrontPorch >> 8) & 0x03) << 6 |
            ((hSyncWidth >> 8) & 0x03) << 4 |
            ((vFrontPorch >> 4) & 0x03) << 2 |
            ((vSyncWidth >> 4) & 0x03)
        );

        // Image Size in mm (bytes 12-14) - Unused for overrides, leave 0

        // H and V Border (bytes 15-16) - 0
        
        // Flags (byte 17)
        byte flags = 0x18; // Digital separate sync by default
        if (interlaced) flags |= 0x80;
        if (hSyncPositive) flags |= 0x02;
        if (vSyncPositive) flags |= 0x04;
        
        dtd[17] = flags;

        return dtd;
    }
}
