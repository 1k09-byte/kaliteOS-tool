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
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class WindhawkInstallerService
{
    private static readonly HttpClient _http = new();

    public const string DirectDownloadUrl = "https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe";
    public const string DirectVersion = "2.0.0-alpha.5";

    private readonly WindhawkDetectionService _detection = new();

    private readonly List<string> _tempFilePaths = new();

    public WindhawkInstallationInfo? LastInstalledInfo { get; private set; }

    public Task<(string Version, string DownloadUrl)> ResolveLatestOfflineInstallerAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult((DirectVersion, DirectDownloadUrl));
    }

    public async Task<string> DownloadInstallerAsync(
        string downloadUrl, IProgress<string>? status = null, CancellationToken ct = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        status?.Report($"Downloading Windhawk {DirectVersion} from GitHub (direct)...");

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("kaliteConfig-WindhawkInstaller/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[1 << 16];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
            await file.WriteAsync(buffer.AsMemory(0, read), ct);

        _tempFilePaths.Add(tempPath);
        return tempPath;
    }

    public async Task InstallSilentlyAsync(
        string installerPath, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). " +
                "Relaunch kaliteConfig elevated and retry.");
        }

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Windhawk installer not found at the expected temp path.", installerPath);

        status?.Report("Installing Windhawk silently...");

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk offline installer.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Windhawk installer exited with code {process.ExitCode}. " +
                "Installation may have failed; check the official site: https://windhawk.net/");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    public async Task<WindhawkInstallerResult> ImportSettingsAsync(
        string jsonPath,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var info = _detection.Detect();
        if (!info.IsInstalled || string.IsNullOrEmpty(info.InstallDirectory))
            throw new InvalidOperationException(
                "Windhawk does not appear to be installed. Install it first before importing settings.");

        string cliPath = info.CliPath
            ?? Path.Combine(info.InstallDirectory, "windhawk-cli.exe");

        if (!File.Exists(cliPath))
        {
            string pfCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk", "windhawk-cli.exe");
            if (File.Exists(pfCli))
                cliPath = pfCli;
        }

        if (!File.Exists(cliPath))
            throw new InvalidOperationException(
                "windhawk-cli.exe not found in the Windhawk install directory. " +
                "The CLI is required to import settings; ensure Windhawk 2.0+ is installed.");

        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Settings JSON file not found.", jsonPath);

        status?.Report("Importing Windhawk settings via windhawk-cli...");

        var importResult = await RunCliCommandAsync(
            cliPath,
            $"data import \"{jsonPath}\" --confirm-app-restart --yes",
            info.InstallDirectory,
            status,
            ct);

        var parsed = ParseImportSummary(importResult.Stdout);
        if (parsed is not null)
        {
            status?.Report(parsed);
        }
        else if (!string.IsNullOrWhiteSpace(importResult.Stderr))
        {
            status?.Report($"Import completed with stderr: {importResult.Stderr.Trim()}");
        }
        else
        {
            status?.Report("Settings imported via windhawk-cli.");
        }

        LastInstalledInfo = _detection.Detect();

        var updateResult = await UpdateModsIfAvailableAsync(cliPath, info.InstallDirectory, status, ct);
        if (updateResult.HasUpdates)
        {
            status?.Report($"Updated {updateResult.UpdatedIds.Count} mod(s) to the latest version.");
        }
        else if (updateResult.CheckedCount > 0)
        {
            status?.Report("All installed mods are up to date.");
        }

        return new WindhawkInstallerResult(
            Success: importResult.ExitCode == 0 || importResult.ExitCode == 7,
            ExitCode: importResult.ExitCode,
            Stdout: importResult.Stdout,
            Stderr: importResult.Stderr,
            ModsUpdated: updateResult.UpdatedIds.ToList());
    }

    public async Task<ModUpdateResult> UpdateModsIfAvailableAsync(
        string cliPath,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var result = new ModUpdateResult();

        status?.Report("Checking for mod updates...");

        var listResult = await RunCliCommandAsync(
            cliPath,
            "mod list --update-available --json",
            workingDirectory,
            status,
            ct);

        if (listResult.ExitCode != 0)
        {
            status?.Report($"Mod update check failed (exit {listResult.ExitCode}): {listResult.Stderr.Trim()}");
            return result;
        }

        var updateable = ParseUpdateAvailableList(listResult.Stdout);
        result.CheckedCount = updateable.Count;

        if (updateable.Count == 0)
            return result;

        foreach (var modId in updateable)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"Updating mod {modId}...");

            var updateResult = await RunCliCommandAsync(
                cliPath,
                $"mod update {modId}",
                workingDirectory,
                status,
                ct);

            if (updateResult.ExitCode == 0)
                result.UpdatedIds.Add(modId);
            else
                status?.Report($"Update failed for {modId} (exit {updateResult.ExitCode}): {updateResult.Stderr.Trim()}");
        }

        result.HasUpdates = result.UpdatedIds.Count > 0;
        return result;
    }

    private static async Task<CliRunResult> RunCliCommandAsync(
        string cliPath,
        string arguments,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start windhawk-cli: {cliPath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("windhawk-cli command timed out after 5 minutes.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return new CliRunResult(process.ExitCode, stdout, stderr);
    }

    private static string? ParseImportSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement? modsElement = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("summary", out var s) && s.TryGetProperty("mods", out var m))
                {
                    modsElement = m;
                }
                else if (root.TryGetProperty("data", out var data) && data.TryGetProperty("summary", out var s2) && s2.TryGetProperty("mods", out var m2))
                {
                    modsElement = m2;
                }
                else
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("mods", out var m3))
                        {
                            modsElement = m3;
                            break;
                        }
                    }
                }
            }

            if (modsElement is null || modsElement.Value.ValueKind != JsonValueKind.Array)
                return null;

            int imported = 0, failed = 0;
            var failures = new List<string>();

            foreach (var mod in modsElement.Value.EnumerateArray())
            {
                string modId = mod.TryGetProperty("modId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
                string status = mod.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                bool ok = status.Equals("installed", StringComparison.OrdinalIgnoreCase);
                if (ok) imported++;
                else
                {
                    failed++;
                    string msg = mod.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? status
                        : status;
                    if (!string.IsNullOrEmpty(modId))
                        failures.Add($"{modId}: {msg}");
                }
            }

            var parts = new List<string> { $"CLI import: {imported} mod(s) installed" };
            if (failed > 0)
                parts.Add($"{failed} failed: " + string.Join("; ", failures));

            return string.Join(" | ", parts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ParseUpdateAvailableList(string json)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return ids;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        ids.Add(el.GetString()!);
                return ids;
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in prop.Value.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                            ids.Add(el.GetString()!);
                }
            }
        }
        catch (Exception)
        {
        }

        return ids;
    }

    public void CleanupTempFiles()
    {
        foreach (var path in _tempFilePaths.ToList())
        {
            TryDeleteWithForce(path);
        }

        _tempFilePaths.Clear();
    }

    public static void CleanupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        TryDeleteWithForce(path);
    }

    private static void TryDeleteWithForce(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                break;
            }
            catch (IOException)
            {
                var locker = FindLockedProcess(path);
                if (locker is not null && locker.Id != Environment.ProcessId)
                {
                    try
                    {
                        locker.Kill(entireProcessTree: true);
                        Task.Delay(500).Wait();
                    }
                    catch { }
                }

                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    break;
                }
                catch
                {
                }
            }
        }
    }

    private static Process? FindLockedProcess(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fileName)))
            {
                try
                {
                    if (proc.MainModule?.FileName.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
                        return proc;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    public record CliRunResult(int ExitCode, string Stdout, string Stderr);

    public record WindhawkInstallerResult(
        bool Success,
        int ExitCode,
        string Stdout,
        string Stderr,
        List<string> ModsUpdated);

    public sealed class ModUpdateResult
    {
        public bool HasUpdates { get; set; }
        public int CheckedCount { get; set; }
        public List<string> UpdatedIds { get; } = new();
    }

    public void Dispose()
    {
        CleanupTempFiles();
    }
}
