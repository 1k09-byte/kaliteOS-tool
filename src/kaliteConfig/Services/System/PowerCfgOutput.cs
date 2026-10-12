// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use, but the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Text.RegularExpressions;

namespace kaliteConfig.Services;

/// <summary>
/// Interprets what <c>powercfg</c> said when a plan was imported.
/// </summary>
public static class PowerCfgOutput
{
    /// <summary>
    /// What powercfg actually prints on success, captured from a real import:
    /// <c>Imported Power Scheme Successfully. GUID: 7e4d1d0e-beae-4cc3-8339-81cf882d51df</c>
    ///
    /// The GUID only exists in this line. Without parsing it, the importer has no
    /// idea which plan it just created - it cannot name it, cannot select it, and
    /// cannot tell a real import from a refusal that happens to exit 0.
    /// </summary>
    private static readonly Regex ImportedGuid = new(
        @"GUID:\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The GUID powercfg reported, or null when it did not report one.</summary>
    public static Guid? ParseImportedGuid(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = ImportedGuid.Match(output);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var guid) ? guid : null;
    }

    /// <summary>
    /// True when an import actually happened: powercfg said so and handed back a
    /// GUID. A non-zero exit is treated as failure regardless of what was printed.
    /// </summary>
    public static bool ImportSucceeded(int exitCode, string? output)
        => exitCode == 0 && ParseImportedGuid(output).HasValue;

    /// <summary>
    /// The line to show the user. Prefers powercfg's own words - it knows why it
    /// said no, and its message is more use than anything we could invent.
    /// </summary>
    public static string Describe(int exitCode, string? output, string fileName)
    {
        if (ImportSucceeded(exitCode, output))
            return $"Imported {fileName}.";

        string said = (output ?? "").Trim();
        if (said.Length == 0)
            return $"Could not import {fileName}. powercfg exited with code {exitCode}.";
        if (exitCode != 0)
            return $"Could not import {fileName}. {said} (powercfg exited with code {exitCode})";
        return $"Could not import {fileName}. powercfg reported success but named no plan. {said}";
    }
}
