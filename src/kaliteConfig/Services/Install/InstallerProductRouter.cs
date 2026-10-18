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
using System;

namespace kaliteConfig.Services;

public static class InstallerProductRouter
{
    public static string Resolve(string? name, string? productId = null)
    {
        if (!string.IsNullOrWhiteSpace(productId)) return productId!;

        string n = name ?? string.Empty;

        if (n.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Epic Games Launcher";
        if (n.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return "Minecraft Launcher";
        if (n.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft Connect";
        if (n.Contains("Helium", StringComparison.OrdinalIgnoreCase)) return "Helium";
        if (n.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (n.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (n.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (n.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (n.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (n.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "Vivaldi";
        if (n.Contains("Zen", StringComparison.OrdinalIgnoreCase)) return "Zen";
        if (n.StartsWith("EA", StringComparison.OrdinalIgnoreCase)) return "EA app";
        return "Unknown";
    }
}
