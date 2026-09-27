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

/// <summary>
/// Resolves an Apps-tab entry to the product it actually installs, which is
/// what the uninstall path keys off (registry lookup, processes to kill,
/// residue to wipe).
///
/// This used to be a chain of <c>name.Contains(...)</c> tests evaluated in
/// order, which silently mis-routed real products: "Steam".Contains("EA",
/// OrdinalIgnoreCase) is TRUE - st-<b>ea</b>-m - and the EA test came first, so
/// uninstalling Steam searched for and wiped the residue of the EA app.
/// "Epic Games Launcher" likewise matched the shorter "Epic" test, and any
/// future rename would re-break it without any compile error.
///
/// Entries now carry an explicit ProductId; this name-based path is only the
/// fallback for entries that predate it.
/// </summary>
public static class InstallerProductRouter
{
    /// <summary>
    /// The product id for an entry, or "Unknown" when nothing matches. Never
    /// throws - a null item or a null name simply resolves to Unknown, which
    /// is the safe answer because the caller then performs no destructive
    /// work.
    /// </summary>
    public static string Resolve(string? name, string? productId = null)
    {
        if (!string.IsNullOrWhiteSpace(productId)) return productId!;

        string n = name ?? string.Empty;

        // Most specific first. "EA app" is matched as a PREFIX only - a bare
        // Contains("EA") would match Steam, Among Us, CleanMyPC, ...
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
