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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class WindhawkImportService
{
    private const string BackupFormat = "windhawk-user-data-v1";

    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(3);

    public static WindhawkBackup ParseBackup(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException("Windhawk backup file not found.", jsonFilePath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        var backup = JsonSerializer.Deserialize<WindhawkBackup>(File.ReadAllText(jsonFilePath), options)
            ?? throw new InvalidOperationException("The Windhawk backup file is not valid JSON.");

        if (!string.Equals(backup.Format, BackupFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported backup format '{backup.Format}' (expected '{BackupFormat}').");

        return backup;
    }

    public async Task<WindhawkImportResult> ImportBackupAsync(
        string jsonFilePath,
        WindhawkInstallationInfo installation,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var backup = ParseBackup(jsonFilePath);

        var cliPath = installation.CliPath;
        if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            throw new InvalidOperationException(
                "This Windhawk install has no windhawk-cli.exe, so backups cannot be applied " +
                "automatically. Update Windhawk (2.0 or newer) and try again.");

        var result = new WindhawkImportResult();

        status?.Report("Checking the backup with Windhawk...");
        var inspect = await RunCliAsync(cliPath, new[] { "--json", "data", "inspect", jsonFilePath }, ct);
        if (!inspect.Success)
        {
            throw new InvalidOperationException(
                "Windhawk rejected this backup file: " + Describe(inspect));
        }

        var expected = ReadManifestModIds(inspect.StdOut);
        if (expected.Count == 0)
        {
            foreach (var mod in backup.Mods)
                if (!string.IsNullOrWhiteSpace(mod.ModId))
                    expected[mod.ModId] = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version;
        }
        status?.Report($"Backup looks valid: {expected.Count} mod(s), {backup.Mods.Count} listed in the file.");

        var declaredDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in backup.Mods)
            if (!string.IsNullOrWhiteSpace(mod.ModId) && mod.Config?.Disabled == true)
                declaredDisabled.Add(mod.ModId);

        status?.Report("Applying the backup via Windhawk...");
        var import = await RunCliAsync(cliPath, new[] { "data", "import", jsonFilePath, "--yes" }, ct);
        if (!import.Success)
        {
            foreach (var modId in expected.Keys)
                result.Outcomes.Add(new WindhawkModImportOutcome(modId, false, "Not applied - Windhawk reported an error."));
            result.Skipped = expected.Count;
            throw new InvalidOperationException(
                "Windhawk could not apply this backup: " + Describe(import));
        }

        status?.Report("Verifying the result with Windhawk...");
        var installed = await ReadInstalledModsAsync(cliPath, ct);

        foreach (var modId in expected.Keys)
        {
            if (!installed.TryGetValue(modId, out var state))
            {
                result.Skipped++;
                result.Outcomes.Add(new WindhawkModImportOutcome(
                    modId, false, "Not present in Windhawk after the import."));
                continue;
            }

            if (!state.Enabled)
            {
                if (declaredDisabled.Contains(modId))
                {
                    result.Imported++;
                    result.Outcomes.Add(new WindhawkModImportOutcome(
                        modId, true,
                        state.Version is null
                            ? "imported (disabled by the bundled config)"
                            : $"v{state.Version} (disabled by the bundled config)"));
                    continue;
                }

                result.Skipped++;
                result.Outcomes.Add(new WindhawkModImportOutcome(
                    modId, false, "Installed but disabled - Windhawk will not apply it until it is enabled."));
                continue;
            }

            result.Imported++;
            result.Outcomes.Add(new WindhawkModImportOutcome(
                modId, true, state.Version is null ? null : $"v{state.Version}"));
        }

        status?.Report(result.SummaryText);
        return result;
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    private sealed record ModState(bool Enabled, string? Version);

    private static async Task<CliResult> RunCliAsync(
        string cliPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start windhawk-cli.exe.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CliTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"windhawk-cli did not finish within {CliTimeout.TotalMinutes:0} minutes.");
        }

        return new CliResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch {  }
    }

    private static string Describe(CliResult r)
    {
        var text = string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr;
        text = text.Trim();
        if (text.Length == 0) return $"exit code {r.ExitCode}.";
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine;
    }

    private static Dictionary<string, string?> ReadManifestModIds(string stdout)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!TryParseJson(stdout, out var root)) return result;

        if (root.ValueKind != JsonValueKind.Object) return result;
        if (!root.TryGetProperty("data", out var data)) return result;
        if (data.ValueKind != JsonValueKind.Object) return result;
        if (!data.TryGetProperty("manifest", out var manifest)) return result;
        if (manifest.ValueKind != JsonValueKind.Object) return result;
        if (!manifest.TryGetProperty("mods", out var mods)) return result;
        if (mods.ValueKind != JsonValueKind.Array) return result;

        foreach (var mod in mods.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object) continue;
            var id = mod.TryGetProperty("modId", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var ver = mod.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
            result[id!] = ver;
        }
        return result;
    }

    private static async Task<Dictionary<string, ModState>> ReadInstalledModsAsync(
        string cliPath, CancellationToken ct)
    {
        var installed = new Dictionary<string, ModState>(StringComparer.OrdinalIgnoreCase);

        var run = await RunCliAsync(cliPath, new[] { "--json", "mod", "list" }, ct);
        if (!run.Success || !TryParseJson(run.StdOut, out var root)) return installed;

        if (root.ValueKind != JsonValueKind.Object) return installed;
        if (!root.TryGetProperty("data", out var data)) return installed;
        if (data.ValueKind != JsonValueKind.Object) return installed;
        if (!data.TryGetProperty("mods", out var mods)) return installed;
        if (mods.ValueKind != JsonValueKind.Array) return installed;

        foreach (var mod in mods.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object) continue;
            var id = mod.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var enabled = mod.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            var version = mod.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

            installed[id!] = new ModState(enabled, version);
        }
        return installed;
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
