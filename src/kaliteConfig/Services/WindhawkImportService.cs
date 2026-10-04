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

/// <summary>
/// Applies a "windhawk-user-data-v1" backup to an installed Windhawk by driving
/// Windhawk's OWN command-line interface (windhawk-cli.exe, shipped beside
/// windhawk.exe since Windhawk 2.0).
///
/// WHY THE CLI AND NOT THE REGISTRY
/// --------------------------------
/// Windhawk persists its profile under HKLM\SOFTWARE\Windhawk, and an earlier
/// version of this file wrote those keys directly. That was wrong: the engine
/// names each mod's binary "&lt;modId&gt;_&lt;version&gt;_&lt;hash&gt;.dll"
/// (e.g. "f1-blocker_0.0.3_989293.dll"), a name only the engine can compute, so
/// a hand-written "LibraryFileName" pointing at "&lt;modId&gt;.dll" resolved to
/// nothing and left mods installed-but-dead. The CLI already implements this
/// correctly and is the interface Windhawk supports, so this class owns no
/// knowledge of the on-disk layout at all: it hands Windhawk a backup file and
/// lets the engine do the work.
///
/// The flow is:
///   1. data inspect  - validate the archive up front and learn what it holds
///                      (read-only; nothing is modified).
///   2. data import   - apply it. One call, atomic from our point of view, and
///                      it carries app settings + mod configs + mod settings.
///   3. mod list      - read the engine's own view back and diff against the
///                      manifest, so the summary reports what Windhawk really
///                      has rather than what we asked for.
/// </summary>
public sealed class WindhawkImportService
{
    private const string BackupFormat = "windhawk-user-data-v1";

    /// <summary>How long any single CLI invocation may take before we give up.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Parses and validates a backup file without touching Windhawk. Kept as a
    /// separate step so the UI can reject a bad file before it prompts for
    /// anything.
    /// </summary>
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

    /// <summary>
    /// Imports a backup into the installed Windhawk.
    /// </summary>
    /// <param name="jsonFilePath">Path to the windhawk-user-data-v1 archive.</param>
    /// <param name="installation">The detected Windhawk install; must carry a CLI path.</param>
    /// <param name="status">Progress text for the UI.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<WindhawkImportResult> ImportBackupAsync(
        string jsonFilePath,
        WindhawkInstallationInfo installation,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        // Parsing first gives a clean error for a malformed file, and it also
        // proves the file is readable before we shell out to anything.
        var backup = ParseBackup(jsonFilePath);

        var cliPath = installation.CliPath;
        if (string.IsNullOrEmpty(cliPath) || !File.Exists(cliPath))
            throw new InvalidOperationException(
                "This Windhawk install has no windhawk-cli.exe, so backups cannot be applied " +
                "automatically. Update Windhawk (2.0 or newer) and try again.");

        var result = new WindhawkImportResult();

        // 1. Validate with the engine's own inspector before changing anything.
        //    --json matters: without it the CLI emits human-readable text and the
        //    manifest below would silently parse to nothing.
        status?.Report("Checking the backup with Windhawk...");
        var inspect = await RunCliAsync(cliPath, new[] { "--json", "data", "inspect", jsonFilePath }, ct);
        if (!inspect.Success)
        {
            throw new InvalidOperationException(
                "Windhawk rejected this backup file: " + Describe(inspect));
        }

        var expected = ReadManifestModIds(inspect.StdOut);
        // If the manifest could not be read (unexpected CLI output shape), fall
        // back to the ids from the file we already parsed - verifying "whatever
        // the backup asked for" is still meaningful, and reporting zero mods
        // after a successful import would be a silent lie.
        if (expected.Count == 0)
        {
            foreach (var mod in backup.Mods)
                if (!string.IsNullOrWhiteSpace(mod.ModId))
                    expected[mod.ModId] = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version;
        }
        status?.Report($"Backup looks valid: {expected.Count} mod(s), {backup.Mods.Count} listed in the file.");

        // 2. Apply. data import is the supported, complete path - it carries app
        //    settings, each mod's config and each mod's runtime settings.
        status?.Report("Applying the backup via Windhawk...");
        var import = await RunCliAsync(cliPath, new[] { "data", "import", jsonFilePath, "--yes" }, ct);
        if (!import.Success)
        {
            // Do NOT fall back to writing the registry ourselves: that path is
            // exactly the thing that silently installed dead mods before. If
            // the engine says no, the user needs to see why.
            foreach (var modId in expected.Keys)
                result.Outcomes.Add(new WindhawkModImportOutcome(modId, false, "Not applied - Windhawk reported an error."));
            result.Skipped = expected.Count;
            throw new InvalidOperationException(
                "Windhawk could not apply this backup: " + Describe(import));
        }

        // 3. Verify against the engine's own view rather than trusting the
        //    import's exit code alone.
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

    // ------------------------------------------------------------------
    // CLI plumbing
    // ------------------------------------------------------------------

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    private sealed record ModState(bool Enabled, string? Version);

    /// <summary>
    /// Runs windhawk-cli with the given arguments and captures both streams.
    /// Uses ArgumentList (never a joined string) so mod ids and setting values
    /// cannot be re-interpreted as flags, and redirects the streams so a hidden
    /// window never flashes on screen.
    /// </summary>
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

        // Read both pipes before awaiting exit: a child that fills a pipe buffer
        // while we wait on exit would otherwise deadlock.
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
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>Best-effort one-line explanation of a failed CLI run.</summary>
    private static string Describe(CliResult r)
    {
        var text = string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr;
        text = text.Trim();
        if (text.Length == 0) return $"exit code {r.ExitCode}.";
        // The CLI is chatty; one line is enough for an InfoBar.
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine;
    }

    // ------------------------------------------------------------------
    // JSON reading
    // ------------------------------------------------------------------

    /// <summary>
    /// Pulls the mod ids out of `data inspect --json`. Returns an empty map when
    /// the output is not the expected shape - the caller still has the parsed
    /// file, so an unexpected payload degrades to "verify whatever is there"
    /// rather than failing the import.
    /// </summary>
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

    /// <summary>
    /// Reads `mod list --json` into id → (enabled, version). This is the
    /// engine's own answer to "what do you actually have?", which is why the
    /// summary is built from it instead of from the import's exit code.
    /// </summary>
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
