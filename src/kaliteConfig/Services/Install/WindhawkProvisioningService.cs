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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class WindhawkProvisioningService
{
    private static readonly HttpClient _httpClient = new();

    public const string DirectDownloadUrl = "https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe";
    public const string DirectVersion = "2.0.0-alpha.5";
    private const string OfficialSiteUrl = "https://windhawk.net/";

    public record ProvisionProgress(WindhawkProvisionStage Stage, string StatusText, double? DownloadPercent);
    public record ResolvedInstaller(string Version, string DownloadUrl, string? Sha256, long SizeBytes);

    public enum WindhawkProvisionStage
    {
        Resolving,
        Downloading,
        Installing,
        Verifying,
        Done,
        Failed,
    }

    private readonly WindhawkDetectionService _detection = new();
    private readonly WindhawkInstallerService _installer = new();

    public Task<ResolvedInstaller> ResolveLatestInstallerAsync(CancellationToken ct)
    {
        var resolved = new ResolvedInstaller(DirectVersion, DirectDownloadUrl, null, 0);
        return Task.FromResult(resolved);
    }

    public async Task<WindhawkInstallationInfo> EnsureInstalledAsync(
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        var existing = _detection.Detect();
        if (existing.IsInstalled && existing.CliPath is not null)
        {
            progress.Report(new ProvisionProgress(
                WindhawkProvisionStage.Done,
                $"Windhawk {existing.Version ?? ""} (2.0+) is already installed.".Trim(),
                null));
            return existing;
        }

        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving, "Resolving latest Windhawk release...", null));
        var installer = await ResolveLatestInstallerAsync(ct);

        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading, $"Downloading Windhawk {installer.Version}...", 0));
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
        await DownloadFileAsync(installer.DownloadUrl, tempPath, installer.SizeBytes, progress, ct);

        if (!string.IsNullOrEmpty(installer.Sha256))
        {
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installer signature...", null));
            await VerifySha256Async(tempPath, installer.Sha256!, ct);
        }

        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, "Installing Windhawk (silent)...", null));
        await RunInstallerSilentlyAsync(tempPath, ct);

        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installation...", null));
        var result = await WaitForInstallationAsync(ct);
        if (!result.IsInstalled)
        {
            throw new InvalidOperationException(
                "The Windhawk installer finished but the installation could not be verified. Check the official site: " + OfficialSiteUrl);
        }
        if (result.CliPath is null)
        {
            throw new InvalidOperationException(
                "Windhawk was installed but windhawk-cli.exe is missing - settings import requires the 2.0 CLI. " +
                "The pinned installer may have been superseded: check " + OfficialSiteUrl);
        }

        try { File.Delete(tempPath); } catch { }

        progress.Report(new ProvisionProgress(
            WindhawkProvisionStage.Done,
            $"Windhawk {result.Version ?? ""} installed.".Trim() + ".",
            null));
        return result;
    }

    public async Task<WindhawkInstallerService.WindhawkInstallerResult> InstallAndImportAsync(
        string jsonPath,
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving,
            $"Using Windhawk {DirectVersion} direct download...", null));

        string version = DirectVersion;
        string downloadUrl = DirectDownloadUrl;

        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading,
            $"Downloading Windhawk {version}...", null));

        string installerPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
        try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        await DownloadFileAsync(downloadUrl, installerPath, 0, progress, ct);

        string effectiveJsonPath = jsonPath;
        if (string.IsNullOrWhiteSpace(effectiveJsonPath) || !File.Exists(effectiveJsonPath))
        {
            effectiveJsonPath = WindhawkModCatalog.EnsureBundledBackupOnDisk();
            if (string.IsNullOrWhiteSpace(effectiveJsonPath) || !File.Exists(effectiveJsonPath))
                throw new FileNotFoundException("KaliteOS Windhawk settings file not found in Assets/Windhawk.", effectiveJsonPath);
        }

        try
        {
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Installing Windhawk (silent)...", null));

            await RunInstallerSilentlyAsync(installerPath, ct);

            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Importing Windhawk settings...", null));

            var importResult = await _installer.ImportSettingsAsync(
                effectiveJsonPath,
                new Progress<string>(s => progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, s, null))),
                ct);

            progress.Report(new ProvisionProgress(
                importResult.Success ? WindhawkProvisionStage.Done : WindhawkProvisionStage.Failed,
                importResult.Success
                    ? $"Windhawk {version} installed and settings imported."
                    : $"Windhawk installed, but settings import had issues (exit {importResult.ExitCode}).",
                null));

            return importResult;
        }
        finally
        {
            _installer.CleanupTempFiles();
            WindhawkInstallerService.CleanupFile(installerPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, long totalBytes, IProgress<ProvisionProgress> progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("kaliteConfig-WindhawkProvisioner/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        totalBytes = response.Content.Headers.ContentLength ?? totalBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTime.UtcNow;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;
            if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new ProvisionProgress(
                    WindhawkProvisionStage.Downloading,
                    $"Downloading Windhawk... ({totalRead * 100.0 / totalBytes:F0}%)",
                    totalRead * 100.0 / totalBytes));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            string actual = Convert.ToHexStringLower(hash);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Downloaded Windhawk installer failed SHA256 verification - download may be corrupted or tampered with. Aborting.");
            }
        }, ct);
    }

    private static async Task RunInstallerSilentlyAsync(string installerPath, CancellationToken ct)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). Relaunch kaliteConfig elevated and retry.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk installer.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        int exit = -1;
        try { if (process.HasExited) exit = process.ExitCode; } catch { }
    }

    private static async Task<WindhawkInstallationInfo> WaitForInstallationAsync(CancellationToken ct)
    {
        var detection = new WindhawkDetectionService();
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            var info = detection.Detect();
            if (info.IsInstalled) return info;
            await Task.Delay(1000, ct);
        }
        return WindhawkInstallationInfo.NotInstalled;
    }
}
