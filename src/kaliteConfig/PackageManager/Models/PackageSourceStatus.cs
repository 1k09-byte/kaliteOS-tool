// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
namespace kaliteConfig.PackageManager.Models;

/// <summary>Backend identifiers. String (not enum) so bundles serialize cleanly.</summary>
public static class PackageSourceIds
{
    public const string WinGet = "winget";
    public const string Scoop = "scoop";
    public const string Chocolatey = "chocolatey";
    public const string Npm = "npm";
    public const string Pip = "pip";
    public const string Cargo = "cargo";
    public const string Vcpkg = "vcpkg";
    public const string DotNetTool = "dotnet-tool";
    public const string PowerShell7 = "powershell-7";
    public const string PowerShell5 = "powershell-5";
    public const string Local = "local";
}

/// <summary>
/// One backend row for the Package Managers preferences page.
/// </summary>
public sealed class PackageSourceStatus
{
    public required string SourceId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public bool IsImplemented { get; init; }
    public bool IsEnabled { get; set; }
    public bool IsDetected { get; set; }
    public string DetectedVersion { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;

    public string StatusText
    {
        get
        {
            if (!IsImplemented) return "Not implemented yet";
            if (!IsDetected) return "Not found";
            return string.IsNullOrWhiteSpace(DetectedVersion) ? "Ready" : $"Ready · {DetectedVersion}";
        }
    }
}
