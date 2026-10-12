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
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>
/// "Start with Windows" for both deployment modes. The rules engine lives
/// in-process (WMI process-start watcher), so rules only apply while the app
/// runs - this keeps it running across logons.
///
/// Login launches always pass --tray so the app starts hidden in the
/// notification area instead of popping a window on every boot/logon.
///
/// Unpackaged: HKCU\...\CurrentVersion\Run value (per the Run-keys doc -
/// command lines there run at user logon).
/// Packaged (MSIX): the exe path is versioned under WindowsApps, so a static
/// Run path would rot on every update - uses the windows.startupTask
/// extension (TaskId below, declared in Package.appxmanifest) instead.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "kaliteConfig";
    private const string StartupTaskId = "KaliteConfigStartup";
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Login-launch flag: start hidden to the tray, no window.</summary>
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

        string? current = ReadRunValue(RunValueName);
        if (current == null)
        {
            return false;
        }

        // Self-heal: dev/unpackaged exe paths move between builds, and older
        // installs lack the --tray login flag. Normalize either form.
        string want = TrayCommandLine();
        string bare = QuotedExePath();
        if (!string.Equals(current, want, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current, bare, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
        {
            WriteRunValue(RunValueName, want);
        }
        return true;
    }

    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            return await SetStartupTaskEnabledAsync(enabled);
        }

        try
        {
            if (enabled)
            {
                WriteRunValue(RunValueName, TrayCommandLine());
            }
            else
            {
                DeleteRunValue(RunValueName);
            }
            return true;
        }
        catch
        {
            return false;
        }
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

    internal static void WriteRunValue(string name, string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Could not open the Run registry key.");
        key.SetValue(name, command, RegistryValueKind.String);
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

    internal static string QuotedExePath()
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
        return $"\"{path}\"";
    }

    /// <summary>Login command line: exe plus the start-hidden-to-tray flag.</summary>
    internal static string TrayCommandLine() => $"{QuotedExePath()} {TrayArg}";

    /// <summary>True when this launch came from a login autostart (Run key flag or packaged startup task).</summary>
    public static bool IsTrayLaunch(string? launchArguments)
    {
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Contains(TrayArg, StringComparer.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(launchArguments)
            && launchArguments.Contains(StartupTaskId, StringComparison.OrdinalIgnoreCase);
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
