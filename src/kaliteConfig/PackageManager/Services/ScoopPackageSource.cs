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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// Scoop backend over scoop.cmd.
/// Uses `scoop export` JSON for installed packages.
/// Uses regex parsing for `scoop search` and `scoop status`.
/// </summary>
public sealed class ScoopPackageSource : IPackageSource
{
    public string SourceId => PackageSourceIds.Scoop;
    public string DisplayName => "Scoop";
    public string Description => "Portable Windows user-space packages.";

    public bool CanSearch => true;
    public bool CanInstall => true;
    public bool CanUpdate => true;
    public bool CanUninstall => true;
    public bool CanListInstalled => true;
    public bool CanListUpdates => true;

    private const string Exe = "scoop.cmd";
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
            var result = await CliProcess.RunAsync("powershell", "-c \"scoop --version\"", 10_000, ct).ConfigureAwait(false);
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
                if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        catch { }
        return "";
    }

    public async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, PackageSearchMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<PackageInfo>();
        var result = await CliProcess.RunAsync("powershell", $"-c \"scoop search '{query}'\"", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"scoop exited with code {result.ExitCode}");
        
        var items = new List<PackageInfo>();
        var searchRegex = new Regex(@"^\s+([a-zA-Z0-9_\-\.]+)\s+\(([^)]+)\)", RegexOptions.Compiled);
        
        foreach (var line in (result.Stdout ?? "").Split('\n')) 
        {
            var m = searchRegex.Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                items.Add(new PackageInfo
                {
                    Id = m.Groups[1].Value,
                    Name = m.Groups[1].Value,
                    SourceId = SourceId,
                    SourceLabel = "Scoop",
                    AvailableVersion = m.Groups[2].Value
                });
            }
        }

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
    
    private sealed class ScoopExportData
    {
        public List<ScoopExportApp> apps { get; set; } = new();
    }
    private sealed class ScoopExportApp
    {
        public string Source { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
    }

    public async Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync("powershell", "-c \"scoop export\"", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"scoop exited with code {result.ExitCode}");
            
        var items = new List<PackageInfo>();
        try 
        {
            var parsed = JsonSerializer.Deserialize<ScoopExportData>(result.Stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.apps != null) 
            {
                foreach(var app in parsed.apps) 
                {
                    items.Add(new PackageInfo
                    {
                        Id = app.Name,
                        Name = app.Name,
                        SourceId = SourceId,
                        SourceLabel = "Scoop",
                        InstalledVersion = app.Version
                    });
                }
            }
        }
        catch { }
        return items;
    }

    public async Task<IReadOnlyList<PackageInfo>> GetAvailableUpdatesAsync(CancellationToken ct = default)
    {
        var result = await CliProcess.RunAsync("powershell", "-c \"scoop status\"", ListTimeoutMs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"scoop exited with code {result.ExitCode}");
            
        var items = new List<PackageInfo>();
        var statusRegex = new Regex(@"^\s+([a-zA-Z0-9_\-\.]+):\s+([^\s]+)\s+->\s+([^\s]+)$", RegexOptions.Compiled);
        
        foreach (var line in (result.Stdout ?? "").Split('\n')) 
        {
            var m = statusRegex.Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                items.Add(new PackageInfo
                {
                    Id = m.Groups[1].Value,
                    Name = m.Groups[1].Value,
                    SourceId = SourceId,
                    SourceLabel = "Scoop",
                    InstalledVersion = m.Groups[2].Value,
                    AvailableVersion = m.Groups[3].Value
                });
            }
        }
        return items;
    }

    public Task<PackageOperationResult> InstallAsync(
        PackageInfo package, string? scope,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        string scopeArg = string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase) ? " -g" : "";
        return RunOperationAsync($"install {package.Id}{scopeArg}", $"Installing {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UpdateAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync($"update {package.Id}", $"Updating {package.DisplayName}…", progress, ct);
    }

    public Task<PackageOperationResult> UninstallAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct)
    {
        return RunOperationAsync($"uninstall {package.Id}", $"Uninstalling {package.DisplayName}…", progress, ct);
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
            var result = await CliProcess.RunAsync("powershell", $"-c \"scoop {arguments}\"", OperationTimeoutMs, ct, lines).ConfigureAwait(false);
            if (result.ExitCode != 0) 
                return PackageOperationResult.Fail($"Scoop exited with code {result.ExitCode}.\n" + result.Stderr);
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
}
