// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use, but the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace kaliteConfig.Native;

/// <summary>
/// Why <see cref="DebugPrivilege"/> ended up where it did, in words a user can act on.
/// The difference between "not elevated" and "elevated but the kernel refused" is the
/// difference between restarting the app as admin and giving up, so they are not
/// collapsed into one "access denied".
/// </summary>
public enum DebugPrivilegeState
{
    /// <summary>SeDebugPrivilege is held and enabled on this process token.</summary>
    Enabled,

    /// <summary>Enabled, but the machine's security policy will not assign it at all.</summary>
    DisabledByPolicy,

    /// <summary>The app is not running elevated, so the privilege is not in its token.</summary>
    NotElevated,

    /// <summary>Something else went wrong talking to advapi32.</summary>
    Failed,
}

internal static class DebugPrivilege
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint PrivilegeEnabledByDefault = 0x00000001;
    private const uint PrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    private static bool _attempted;
    private static DebugPrivilegeState _state = DebugPrivilegeState.Failed;
    private static string _detail = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string? name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    /// <summary>Where the privilege ended up. See <see cref="DebugPrivilegeState"/>.</summary>
    public static DebugPrivilegeState State => _state;

    /// <summary>Why, in a sentence. Empty when everything is fine.</summary>
    public static string Detail => _detail;

    public static bool IsEnabled => _state == DebugPrivilegeState.Enabled;

    /// <summary>
    /// The sentence to show when a handle to a process or thread is refused. Without
    /// this the app says "protected process?" and leaves the user guessing whether
    /// they need to restart as administrator, which is almost always the answer.
    /// </summary>
    public static string AccessDeniedHint => _state switch
    {
        DebugPrivilegeState.Enabled =>
            "Windows refused the handle even with debug rights, so this process is protected by its anti-cheat. Nothing can be changed on it while it is running.",
        DebugPrivilegeState.NotElevated =>
            "Windows refused the handle because this app is not running elevated. Restart kaliteConfig as administrator and try again.",
        DebugPrivilegeState.DisabledByPolicy =>
            "Windows refused the handle and SeDebugPrivilege is disabled by this machine's security policy, so it cannot be enabled.",
        _ =>
            "Windows refused the handle and debug rights could not be turned on, so this process is likely protected by its anti-cheat.",
    };

    /// <summary>
    /// Turns on SeDebugPrivilege once per process. Call it at startup, before any
    /// handle is opened.
    ///
    /// This is what makes a game tunable. A process running at a higher integrity
    /// level than the caller cannot be opened at all without it, so every
    /// OpenProcess(PROCESS_SET_INFORMATION) came back ERROR_ACCESS_DENIED and the
    /// priority, boost and affinity controls reported "no access" for exactly the
    /// processes a tuner exists for. Administrators alone are not enough - the
    /// privilege has to be held *and* enabled, and an admin token holds it disabled
    /// by default.
    ///
    /// Best effort by design: a failure here downgrades the app to what it could
    /// always do, and is reported rather than thrown so a locked-down machine still
    /// runs the app.
    /// </summary>
    public static DebugPrivilegeState EnsureEnabled()
    {
        if (_attempted) return _state;
        _attempted = true;
        _state = DebugPrivilegeState.Failed;
        _detail = "";

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
            {
                _detail = $"OpenProcessToken failed ({Marshal.GetLastWin32Error()}).";
                return _state;
            }

            using (token)
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out Luid luid))
                {
                    _detail = $"SeDebugPrivilege is not known to this system ({Marshal.GetLastWin32Error()}).";
                    _state = DebugPrivilegeState.DisabledByPolicy;
                    return _state;
                }

                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privilege = new LuidAndAttributes
                    {
                        Luid = luid,
                        Attributes = PrivilegeEnabled | PrivilegeEnabledByDefault,
                    },
                };

                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    _detail = $"AdjustTokenPrivileges failed ({Marshal.GetLastWin32Error()}).";
                    return _state;
                }

                // AdjustTokenPrivileges returns TRUE even when it assigned nothing.
                // The privilege is simply absent from the token, which is the normal
                // state for an app that is not running elevated - and the only
                // signal is ERROR_NOT_ALL_ASSIGNED in the last error.
                //
                // The SE_PRIVILEGE_REMOVED bit in the returned state is NOT that
                // signal: it is only filled in when the caller supplies a real
                // previous-state buffer, and this call passes IntPtr.Zero, so the
                // struct comes back untouched with the attributes still set. Reading
                // it here would report success for an app that has no debug rights
                // at all, and every protected process would then be blamed on
                // anti-cheat. Verified live against a token that does not hold
                // SeDebugPrivilege: returns TRUE, err 1300, bit not set.
                int lastError = Marshal.GetLastWin32Error();
                if (lastError == ErrorNotAllAssigned)
                {
                    _state = DebugPrivilegeState.NotElevated;
                    _detail = "SeDebugPrivilege is not held by this token - the app is not running elevated.";
                    return _state;
                }

                if (lastError != 0)
                {
                    _detail = $"AdjustTokenPrivileges reported {lastError}.";
                    return _state;
                }

                _state = DebugPrivilegeState.Enabled;
                _detail = "";
                return _state;
            }
        }
        catch (Exception ex)
        {
            _state = DebugPrivilegeState.Failed;
            _detail = ex.Message;
            return _state;
        }
    }

    /// <summary>
    /// A short, user-facing line about debug rights, for status areas. Empty when
    /// there is nothing to say.
    /// </summary>
    public static string StatusLine => _state switch
    {
        DebugPrivilegeState.Enabled => "",
        DebugPrivilegeState.NotElevated =>
            "Debug rights are off - this app is not running elevated, so protected processes cannot be changed.",
        DebugPrivilegeState.DisabledByPolicy =>
            "SeDebugPrivilege is disabled by policy on this machine, so protected processes cannot be changed.",
        _ => _detail.Length == 0
            ? "Debug rights could not be enabled, so protected processes may refuse changes."
            : $"Debug rights could not be enabled ({_detail}). Protected processes may refuse changes.",
    };
}

internal sealed class SafeAccessTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeAccessTokenHandle() : base(true) { }

    // Declared here rather than reusing NativeMethods.Handles.CloseHandle: that
    // one is a LibraryImport, and pulling it in would drag the whole generated
    // unsafe interop layer along with it for a single CloseHandle.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    protected override bool ReleaseHandle() => CloseHandle(handle);
}
