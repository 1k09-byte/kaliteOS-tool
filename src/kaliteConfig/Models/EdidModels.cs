// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;

namespace kaliteConfig.Models;

public class EdidBlock
{
    public byte[] Data { get; set; } = new byte[128];
    public bool IsExtension { get; set; }

    public EdidBlock(byte[] data, bool isExtension = false)
    {
        if (data.Length != 128) throw new ArgumentException("EDID block must be 128 bytes.");
        Array.Copy(data, Data, 128);
        IsExtension = isExtension;
    }

    public void UpdateChecksum()
    {
        int sum = 0;
        for (int i = 0; i < 127; i++)
        {
            sum += Data[i];
        }
        Data[127] = (byte)(256 - (sum % 256));
    }
}

