// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// Chocolatey backend over choco.exe CLI.
/// Uses the `-r` flag for robust limit-output parsing (pipe delimited).
/// </summary>
public sealed class ChocolateyPackageSource : IPackageSource
{
    public string SourceId => PackageSourceIds.Chocolatey;
    public string DisplayName => "Chocolatey";
    public string Description => "Community Windows packages (CLI parsing).";

    public bool CanSearch => true;
    public bool CanInstall => true;
    public bool CanUpdate => true;
    public bool CanUninstall => true;
    public bool CanListInstalled => true;
    public bool CanListUpdates => true;

    private const string Exe = "choco";
    private const int ListTimeoutMs = 60_000;
    private const int OperationTimeoutMs = 30 * 60_000;

    public async Task<PackageSourceStatus> DetectAsync(CancellationToken ct = default)
    {
        var status = new PackageSourceStatus
        {
            SourceId = SourceId, DisplayName = DisplayName, Description = Description,
            IsImplemented = true, IsEnabled = true,
        };
        try
        {
            var result = await CliProcess.RunAsync(Exe, "--version", 10_000, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) return status;
            status.IsDetected = true;
            status.DetectedVersion = result.Stdout.Trim().Split('\n')[0].Trim();
            status.ExecutablePath = await LocateExecutableAsync(ct).ConfigureAwait(false);
        }
        catch { /* undetected */ }
        return status;
    }

    private async Task<string> LocateExecutableAsync(CancellationToken ct)
    {
        try
        {
            var result = await CliProcess.RunAsync("where", Exe, 10_000, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) return "";
            foreach (string line in result.Stdout.Split('\n'))
            {
                string path = line.Trim().TrimEnd('\r');
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        catch { }
        return "";
    }

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, PackageSearchMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<PackageInfo>();
        var result = await CliProcess.RunAsync(Exe, $"search \"{query}\" -r -y", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"choco exited with code {result.ExitCode}");
        
        var items = ParsePipedRows(result.Stdout, PackageSourceIds.Chocolatey, "Chocolatey", false);
        if (mode == PackageSearchMode.Similar && items.Count > 1)
        {
            string q = query.Trim();
            items = items
                .OrderByDescending(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return items;
    }

    public async Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync(Exe, "list --local-only -r -y", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"choco exited with code {result.ExitCode}");
        return ParsePipedRows(result.Stdout, PackageSourceIds.Chocolatey, "Chocolatey", false);
    }

    public async Task<IReadOnlyList<PackageInfo>> GetAvailableUpdatesAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync(Exe, "outdated -r -y", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"choco exited with code {result.ExitCode}");
        return ParsePipedRows(result.Stdout, PackageSourceIds.Chocolatey, "Chocolatey", true);
    }

    public Task<PackageOperationResult> InstallAsync(
        PackageInfo package, string? scope,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync($"install \"{package.Id}\" -y", $"Installing {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UpdateAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync($"upgrade \"{package.Id}\" -y", $"Updating {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UninstallAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync($"uninstall \"{package.Id}\" -y", $"Uninstalling {package.DisplayName}…", progress, ct);
    }

    private async Task<PackageOperationResult> RunOperationAsync(
        string arguments, string phase,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new PackageOperationProgress { Message = phase });
        try
        {
            var lines = new Progress<string>(line =>
            {
                string tail = line.Trim();
                if (tail.Length > 0)
                    progress?.Report(new PackageOperationProgress { Message = tail });
            });
            var result = await CliProcess.RunAsync(Exe, arguments, OperationTimeoutMs, ct, lines).ConfigureAwait(false);
            if (result.ExitCode != 0 && result.ExitCode != 3010) // 3010 is sometimes reboot required
                return PackageOperationResult.Fail($"Chocolatey exited with code {result.ExitCode}.\n" + result.Stderr);
            return PackageOperationResult.Ok("Done.");
        }
        catch (OperationCanceledException)
        {
            return PackageOperationResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            return PackageOperationResult.Fail(ex.Message);
        }
    }

    internal static List<PackageInfo> ParsePipedRows(string output, string sourceId, string sourceLabel, bool isOutdated)
    {
        var items = new List<PackageInfo>();
        string[] lines = (output ?? "").Split('\n');
        foreach(var rawLine in lines) 
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line) || !line.Contains('|')) continue;
            // Ignore choco upgrade warnings about broken packages
            if (line.StartsWith("warning", StringComparison.OrdinalIgnoreCase)) continue;
            
            var parts = line.Split('|');
            if (parts.Length >= 2) 
            {
                var pi = new PackageInfo
                {
                    Id = parts[0],
                    Name = parts[0],
                    SourceId = sourceId,
                    SourceLabel = sourceLabel,
                    // search/list: `<id>|<version>` => map version to AvailableVersion so it shows in UI for installs, or InstalledVersion
                    // To follow winget roughly, we'll place it in InstalledVersion for local-only, but AvailableVersion for search
                    InstalledVersion = isOutdated ? parts[1] : parts[1],
                    AvailableVersion = (isOutdated && parts.Length >= 3) ? parts[2] : "" // If search, we could map it to Available but Installer UI deals with PackageInfo differently
                };
                items.Add(pi);
            }
        }
        return items;
    }
}
