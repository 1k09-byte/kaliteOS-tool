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
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace kaliteConfig.Services;

public sealed record WindhawkInstallationInfo
{
    public bool IsInstalled { get; init; }
    public string? Version { get; init; }
    public string? InstallDirectory { get; init; }
    public string? CliPath { get; init; }
    public bool IsPortable { get; init; }

    public static readonly WindhawkInstallationInfo NotInstalled = new();
}

public sealed class WindhawkDetectionService
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] KnownInstallDirectories =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk"),
        @"C:\Program Files\Windhawk",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Windhawk"),
    };

    public static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public WindhawkInstallationInfo Detect()
    {
        var info = DetectFromUninstallRegistry();

        if (!info.IsInstalled)
            info = DetectFromKnownPaths();

        return info;
    }

    private static WindhawkInstallationInfo DetectFromUninstallRegistry()
    {
        foreach (var baseKey in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(baseKey, RegistryView.Registry64);
                using var uninstall = root.OpenSubKey(UninstallKeyPath);
                if (uninstall is null) continue;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var subKey = uninstall.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(displayName) ||
                        !displayName.Equals("Windhawk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? version = subKey?.GetValue("DisplayVersion") as string;
                    string? installLocation = subKey?.GetValue("InstallLocation") as string;
                    string? uninstallString = subKey?.GetValue("UninstallString") as string;

                    var installDir = ResolveInstallDirectory(installLocation, uninstallString);
                    var cli = FindCli(installDir);

                    return new WindhawkInstallationInfo
                    {
                        IsInstalled = true,
                        Version = string.IsNullOrWhiteSpace(version) ? null : version,
                        InstallDirectory = installDir,
                        CliPath = cli,
                        IsPortable = false,
                    };
                }
            }
            catch (Exception)
            {
            }
        }

        return WindhawkInstallationInfo.NotInstalled;
    }

    private static WindhawkInstallationInfo DetectFromKnownPaths()
    {
        foreach (var dir in KnownInstallDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            string? exe = FindWindhawkExe(dir);
            if (exe is null) continue;

            var version = TryReadFileVersion(exe);
            return new WindhawkInstallationInfo
            {
                IsInstalled = true,
                Version = version,
                InstallDirectory = dir,
                CliPath = FindCli(dir),
                IsPortable = dir.Contains("ProgramData", StringComparison.OrdinalIgnoreCase),
            };
        }

        return WindhawkInstallationInfo.NotInstalled;
    }

    private static string? ResolveInstallDirectory(string? installLocation, string? uninstallString)
    {
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            return installLocation.TrimEnd('\\');

        if (!string.IsNullOrWhiteSpace(uninstallString))
        {
            var candidate = uninstallString.Trim();
            if (candidate.StartsWith('"'))
            {
                int end = candidate.IndexOf('"', 1);
                if (end > 1) candidate = candidate[1..end];
            }
            else
            {
                int space = candidate.IndexOf(' ');
                if (space > 0) candidate = candidate[..space];
            }

            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate);
        }

        foreach (var dir in KnownInstallDirectories)
        {
            if (Directory.Exists(dir) && FindWindhawkExe(dir) is not null) return dir;
        }

        return null;
    }

    private static string? FindWindhawkExe(string directory)
    {
        foreach (var name in new[] { "windhawk.exe", "Windhawk.exe" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindCli(string? installDirectory)
    {
        if (string.IsNullOrEmpty(installDirectory)) return null;
        foreach (var name in new[] { "windhawk-cli.exe", "windhawk-cli.com" })
        {
            var candidate = Path.Combine(installDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? TryReadFileVersion(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
