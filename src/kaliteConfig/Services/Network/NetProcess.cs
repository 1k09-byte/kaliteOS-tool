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
using System.Text;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>Awaited console processes with a timeout and a kill switch, so no
/// netsh/ping/arp child can outlive its caller or freeze the UI.</summary>
public static class NetProcess
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    public static async Task<Result> RunAsync(string file, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            if (!proc.Start()) return new Result(-1, "", "Could not start " + file, false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
        var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.OutputDataReceived += (_, e) => { if (e.Data is null) outDone.TrySetResult(true); else stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is null) errDone.TrySetResult(true); else stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            var exited = await Task.Run(() => proc.WaitForExit(timeoutMs)).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new Result(-1, stdout.ToString(), "Timed out after " + timeoutMs + " ms; process killed.", true);
            }
            await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);
            return new Result(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (Exception ex)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new Result(-1, stdout.ToString(), ex.Message, false);
        }
    }
}
