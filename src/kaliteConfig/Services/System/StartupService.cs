using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

public sealed class StartupService
{

    private const string StartupTaskId = "KaliteConfigStartup";

    public const string ScheduledTaskName = "kaliteConfig";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "kaliteConfig";

    private const int ErrorInsufficientBuffer = 122;

    public const string TrayArg = "--tray";

    public static bool IsPackaged
    {
        get
        {
            try
            {
                uint len = 0;
                return Native.Kernel32.GetCurrentPackageFullName(ref len, IntPtr.Zero) == ErrorInsufficientBuffer;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            return await IsStartupTaskEnabledAsync();
        }

        bool current = IsScheduledTaskCurrent();
        if (current && ReadRunValue(RunValueName) != null)
        {
            DeleteRunValue(RunValueName);
        }
        return current;
    }

    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            return await SetStartupTaskEnabledAsync(enabled);
        }

        try
        {
            DeleteRunValue(RunValueName);
            return enabled ? RegisterScheduledTask() : DeleteScheduledTask();
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsScheduledTaskCurrent()
    {
        try
        {
            if (!RunSchtasks($"/query /tn \"{ScheduledTaskName}\" /xml ONE", out string xml))
            {
                return false;
            }

            string exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }
            return xml.Contains(exe, StringComparison.OrdinalIgnoreCase)
                && xml.Contains(TrayArg, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool RegisterScheduledTask()
    {
        string exe = ExecutablePath();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return false;
        }

        string args = $"/create /tn \"{ScheduledTaskName}\" " +
                      $"/tr \"\\\"{exe}\\\" {TrayArg}\" " +
                      "/sc onlogon /delay 0000:15 /rl highest /f";
        if (!RunSchtasks(args, out _))
        {
            return false;
        }
        return IsScheduledTaskCurrent();
    }

    internal static bool DeleteScheduledTask()
    {
        RunSchtasks($"/delete /tn \"{ScheduledTaskName}\" /f", out _);
        return !RunSchtasks($"/query /tn \"{ScheduledTaskName}\"", out _);
    }

    internal static string? ReadRunValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    internal static void DeleteRunValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    internal static string ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                path = proc.MainModule?.FileName;
            }
            catch
            {
            }
        }
        return path ?? string.Empty;
    }

    public static bool IsTrayLaunch(string? launchArguments)
    {
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Contains(TrayArg, StringComparer.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(launchArguments)
            && launchArguments.Contains(StartupTaskId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RunSchtasks(string args, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            output = stdout + stderr;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsStartupTaskEnabledAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            return task.State == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> SetStartupTaskEnabledAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return true;
            }

            var state = await task.RequestEnableAsync();
            return state == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        catch
        {
            return false;
        }
    }
}
