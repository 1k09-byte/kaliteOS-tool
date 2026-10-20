using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace kaliteConfig.Services;

public sealed record RecoveryResult(bool Ok, string Message);

public sealed class RecoveryService
{
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "KaliteClearSafeboot";

    public bool IsSafeBootArmed()
    {
        var (ok, output) = RunElevatedTool("bcdedit.exe", "/enum {current}");
        return ok && output.IndexOf("safeboot", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public RecoveryResult RebootToFirmware()
        => Reboot("shutdown.exe", "/r /fw /t 0", "Restarting to UEFI firmware settings.");

    public RecoveryResult RebootToSafeModeOnce()
    {
        var set = RunElevatedTool("bcdedit.exe", "/set {current} safeboot minimal");
        if (!set.Ok) return new RecoveryResult(false, "Could not arm safe mode: " + set.Output);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            key?.SetValue(RunOnceValue, "cmd.exe /c bcdedit /deletevalue {current} safeboot", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            return new RecoveryResult(false, "Safe mode was armed but the one-time cleanup could not be scheduled: " + ex.Message);
        }
        return Reboot("shutdown.exe", "/r /t 0", "Restarting in safe mode once. Normal startup is restored automatically afterwards.");
    }

    public RecoveryResult RebootToSafeModeAlways()
    {
        var set = RunElevatedTool("bcdedit.exe", "/set {current} safeboot minimal");
        if (!set.Ok) return new RecoveryResult(false, "Could not arm safe mode: " + set.Output);
        return Reboot("shutdown.exe", "/r /t 0", "Restarting in safe mode. Every restart will use safe mode until normal startup is restored.");
    }

    public RecoveryResult RestoreNormalStartup()
    {
        var clear = RunElevatedTool("bcdedit.exe", "/deletevalue {current} safeboot");
        try { using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true); key?.DeleteValue(RunOnceValue, throwOnMissingValue: false); } catch { }
        if (!clear.Ok && !clear.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new RecoveryResult(false, "Could not restore normal startup: " + clear.Output);
        return new RecoveryResult(true, "Normal startup restored. The next restart boots Windows normally.");
    }

    private static RecoveryResult Reboot(string file, string args, string message)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return new RecoveryResult(true, message);
        }
        catch (Exception ex)
        {
            return new RecoveryResult(false, "Could not restart: " + ex.Message);
        }
    }

    private static (bool Ok, string Output) RunElevatedTool(string file, string args)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
