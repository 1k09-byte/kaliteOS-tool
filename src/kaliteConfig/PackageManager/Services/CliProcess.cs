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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// Shared subprocess runner for CLI-backed package sources. Both pipes are
/// drained from process start (a child blocked on a full output buffer never
/// exits), with linked timeout/cancellation and a kill on timeout.
/// </summary>
internal static class CliProcess
{
    public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    public static async Task<CliResult> RunAsync(
        string fileName, string arguments, int timeoutMs, CancellationToken ct,
        IProgress<string>? outputLines = null)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!proc.Start())
            throw new InvalidOperationException($"Could not start {fileName}.");

        Task<string> stdoutTask = DrainAsync(proc.StandardOutput, outputLines, ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{fileName} timed out after {timeoutMs / 1000}s.");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new CliResult(proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task<string> DrainAsync(
        System.IO.StreamReader reader, IProgress<string>? outputLines, CancellationToken ct)
    {
        if (outputLines is null) return await reader.ReadToEndAsync().ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            sb.AppendLine(line);
            outputLines.Report(line);
        }
        return sb.ToString();
    }
}
