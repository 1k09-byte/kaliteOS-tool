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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>Capture durations (presets + manual stop).</summary>
public enum BenchmarkDuration
{
    Manual = 0,
    Seconds30 = 30,
    Seconds60 = 60,
    Minutes2 = 120,
}

/// <summary>Recording state machine: Idle → Armed → Recording → Finalizing → Done | Failed.</summary>
public enum CaptureState
{
    Idle,
    Armed,
    Recording,
    Finalizing,
    Done,
    Failed,
}

/// <summary>Result of one capture session.</summary>
public sealed class BenchmarkCaptureResult
{
    public bool Success { get; init; }
    public float[] FrametimesMs { get; init; } = Array.Empty<float>();
    public double[] TimestampsMs { get; init; } = Array.Empty<double>();
    public string CsvPath { get; init; } = string.Empty;
    public int TargetPid { get; init; }
    public string ExeName { get; init; } = string.Empty;
    public double ElapsedSec { get; init; }
    public int RowsTotal { get; init; }
    public int RowsKept { get; init; }
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// CapFrameX-style capture via the bundled PresentMon 2.5.1 console build.
/// Flags used verbatim from `PresentMon.exe --help` on that build; CSV
/// columns resolved by header lookup (<see cref="BenchmarkPresentMonCsv"/>).
/// Session name: kaliteBench. All blocking work runs off the UI thread.
/// </summary>
public sealed class BenchmarkCaptureService
{
    // Pinned 2.5.1 SHA-256 (see Assets/PresentMon/VERSION.txt).
    internal const string PinnedSha256 = "9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191";
    internal const string SessionName = "kaliteBench";
    private const string ExeRelPath = "PresentMon/PresentMon.exe";

    // Well-known SID: Performance Log Users (S-1-5-32-559).
    private static readonly SecurityIdentifier PerfLogUsersSid = new("S-1-5-32-559");

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public event Action<CaptureState>? StateChanged;

    private void SetState(CaptureState next)
    {
        State = next;
        try { StateChanged?.Invoke(next); } catch { }
    }

    public static string ResolveExePath()
    {
        string nextToApp = Path.Combine(AppContext.BaseDirectory, ExeRelPath);
        if (File.Exists(nextToApp)) return nextToApp;
        string inAssets = Path.Combine(AppContext.BaseDirectory, "Assets", ExeRelPath);
        if (File.Exists(inAssets)) return inAssets;
        return nextToApp;
    }

    /// <summary>Verifies the bundled exe exists and matches the pinned hash.</summary>
    public static void VerifyExe()
    {
        string path = ResolveExePath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"PresentMon was not found (expected {path}). " +
                "Reinstall kaliteConfig or restore Assets/PresentMon/PresentMon.exe (v2.5.1).");
        }
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        string actual = Convert.ToHexString(sha.ComputeHash(fs));
        if (!actual.Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"PresentMon hash mismatch (expected {PinnedSha256}, got {actual}). " +
                "The bundled build was replaced or corrupted - restore Assets/PresentMon/PresentMon.exe (v2.5.1) before capturing.");
        }
    }

    /// <summary>
    /// ETW capture needs admin OR membership in Performance Log Users.
    /// Returns (true, "") when capture is allowed, else (false, reason + fix).
    /// </summary>
    public static (bool Ok, string Message) CheckEtwAccess()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                return (true, string.Empty);
            foreach (var g in identity.Groups ?? Enumerable.Empty<IdentityReference>())
            {
                try
                {
                    if (g is SecurityIdentifier sid && sid == PerfLogUsersSid)
                        return (true, string.Empty);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Could not check capture rights: {ex.Message}");
        }
        return (false,
            "PresentMon needs ETW rights: run as administrator, or one-time setup: " +
            "open an elevated prompt and run " +
            "net localgroup \"Performance Log Users\" \"%USERNAME%\" /add " +
            "then sign out and back in. Afterwards captures work unelevated.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Foreground window → PID + exe name (hotkey target).</summary>
    public static bool TryGetForegroundProcess(out int pid, out string exeName, out string error)
    {
        pid = 0; exeName = string.Empty; error = string.Empty;
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { error = "No foreground window."; return false; }
            GetWindowThreadProcessId(hwnd, out uint id);
            if (id == 0) { error = "Foreground window has no process."; return false; }
            using var proc = Process.GetProcessById((int)id);
            pid = proc.Id;
            exeName = proc.ProcessName + ".exe";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not read the foreground window: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Captures frametimes for a target PID. State machine runs
    /// Idle → Armed (countdown via --delay) → Recording → Finalizing → Done|Failed.
    /// </summary>
    public async Task<BenchmarkCaptureResult> CaptureAsync(
        int pid,
        string exeName,
        BenchmarkDuration duration,
        int countdownSec,
        string outputCsvPath,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        VerifyExe();
        var (ok, msg) = CheckEtwAccess();
        if (!ok) throw new UnauthorizedAccessException(msg);

        try { using var probe = Process.GetProcessById(pid); }
        catch { throw new InvalidOperationException($"Target process {pid} ({exeName}) is not running."); }

        string exe = ResolveExePath();
        var sw = Stopwatch.StartNew();
        SetState(CaptureState.Armed);

        string args = $"--session_name {SessionName} --stop_existing_session " +
                      $"--process_id {pid} --output_file \"{outputCsvPath}\" --no_console_stats " +
                      $"--exclude_dropped --terminate_on_proc_exit " +
                      (countdownSec > 0 ? $"--delay {countdownSec} " : string.Empty) +
                      (duration != BenchmarkDuration.Manual
                          ? $"--timed {(int)duration} --terminate_after_timed"
                          : string.Empty);
        progress?.Report($"Starting PresentMon (session {SessionName})…");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            },
        };
        try
        {
            if (!proc.Start())
                throw new InvalidOperationException("PresentMon did not start.");
        }
        catch (Exception ex)
        {
            SetState(CaptureState.Failed);
            throw new InvalidOperationException($"PresentMon did not start: {ex.Message}");
        }

        SetState(CaptureState.Recording);
        progress?.Report($"Recording {exeName} (PID {pid})…");

        try
        {
            if (duration == BenchmarkDuration.Manual)
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // --timed + --terminate_after_timed ends the process itself.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds((int)duration + countdownSec + 30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Manual stop or caller cancel: end the ETW session, then kill as fallback.
            await StopSessionAsync(exe).ConfigureAwait(false);
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(new CancellationTokenSource(5000).Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        SetState(CaptureState.Finalizing);
        sw.Stop();
        progress?.Report("Parsing capture…");

        BenchmarkCaptureResult result = await Task.Run(() =>
        {
            if (!File.Exists(outputCsvPath) || new FileInfo(outputCsvPath).Length == 0)
            {
                return new BenchmarkCaptureResult
                {
                    Success = false,
                    CsvPath = outputCsvPath,
                    TargetPid = pid,
                    ExeName = exeName,
                    ElapsedSec = sw.Elapsed.TotalSeconds,
                    Error = EmptyOutputDiagnosis(pid, exeName),
                };
            }
            BenchmarkPresentMonCsv.ParsedCapture parsed;
            try
            {
                parsed = BenchmarkPresentMonCsv.ParseFile(outputCsvPath, pid);
            }
            catch (Exception ex)
            {
                return new BenchmarkCaptureResult
                {
                    Success = false,
                    CsvPath = outputCsvPath,
                    TargetPid = pid,
                    ExeName = exeName,
                    ElapsedSec = sw.Elapsed.TotalSeconds,
                    Error = $"Could not parse the PresentMon CSV: {ex.Message}",
                };
            }
            if (parsed.RowsKept == 0)
            {
                return new BenchmarkCaptureResult
                {
                    Success = false,
                    CsvPath = outputCsvPath,
                    TargetPid = pid,
                    ExeName = exeName,
                    ElapsedSec = sw.Elapsed.TotalSeconds,
                    RowsTotal = parsed.RowsTotal,
                    Error = EmptyOutputDiagnosis(pid, exeName, parsed),
                };
            }
            return new BenchmarkCaptureResult
            {
                Success = true,
                FrametimesMs = parsed.FrametimesMs,
                TimestampsMs = parsed.TimestampsMs,
                CsvPath = outputCsvPath,
                TargetPid = pid,
                ExeName = exeName,
                ElapsedSec = sw.Elapsed.TotalSeconds,
                RowsTotal = parsed.RowsTotal,
                RowsKept = parsed.RowsKept,
            };
        }, CancellationToken.None).ConfigureAwait(false);

        SetState(result.Success ? CaptureState.Done : CaptureState.Failed);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>Ends a running session (manual-stop path) via the real flag.</summary>
    private static async Task StopSessionAsync(string exe)
    {
        try
        {
            using var stopper = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--session_name {SessionName} --terminate_existing_session",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                },
            };
            stopper.Start();
            await stopper.WaitForExitAsync(new CancellationTokenSource(10000).Token).ConfigureAwait(false);
        }
        catch { }
    }

    internal static string EmptyOutputDiagnosis(
        int pid, string exeName, BenchmarkPresentMonCsv.ParsedCapture? parsed = null)
    {
        string detail = parsed != null
            ? $" (CSV had {parsed.RowsTotal} rows, kept {parsed.RowsKept}, " +
              $"other-PID {parsed.RowsSkippedOtherPid}, bad values {parsed.RowsSkippedBadValue})"
            : " (no CSV was written)";
        return "PresentMon recorded no frames for the target" + detail + ". Likely causes: " +
               "1) wrong process - pick the game exe, not its launcher; " +
               $"2) target exited early (PID {pid}, {exeName}); " +
               "3) no presents during the window - keep the game visible, not minimized; " +
               "4) missing ETW rights - run elevated or join Performance Log Users; " +
               "5) a stale session conflict - retry, the app passes --stop_existing_session automatically.";
    }
}
