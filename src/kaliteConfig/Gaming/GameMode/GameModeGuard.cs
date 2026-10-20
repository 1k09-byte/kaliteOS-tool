using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using kaliteConfig.Native;

namespace kaliteConfig.Gaming.GameMode;

public sealed record GuardVerdict(bool Allowed, string Reason);

public sealed record ProcessFact(
    int Pid,
    int ParentPid,
    string Name,
    string? ImagePath,
    long CreationTicks,
    int SessionId);

public sealed class GameModeGuard
{
    private readonly object _gate = new();
    private HashSet<int> _servicePids = new();
    private DateTime _serviceRefreshUtc = DateTime.MinValue;
    private readonly int _selfPid = Environment.ProcessId;
    private readonly string _selfSid;
    private readonly string _selfName;

    public GameModeGuard()
    {
        _selfSid = CurrentSid();
        _selfName = Path.GetFileName(Environment.ProcessPath ?? "kaliteConfig.exe");
    }

    public IReadOnlyCollection<int> SnapshotServicePids()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _serviceRefreshUtc < TimeSpan.FromSeconds(30)) return _servicePids;
        }
        var found = ReadServicePids();
        lock (_gate)
        {
            _servicePids = found;
            _serviceRefreshUtc = DateTime.UtcNow;
            return _servicePids;
        }
    }

    public GuardVerdict Evaluate(ProcessFact fact, IReadOnlySet<int> gamePids, IReadOnlySet<string> gamePaths, GameModeSettings settings)
    {
        if (fact.Pid <= 4) return new GuardVerdict(false, "System process");
        if (fact.Pid == _selfPid) return new GuardVerdict(false, "kaliteConfig itself");
        if (string.Equals(fact.Name, _selfName, StringComparison.OrdinalIgnoreCase))
            return new GuardVerdict(false, "kaliteConfig helper");
        if (gamePids.Contains(fact.Pid))
            return new GuardVerdict(false, "The game, its launcher or its children");
        if (!string.IsNullOrEmpty(fact.ImagePath) && gamePaths.Contains(fact.ImagePath))
            return new GuardVerdict(false, "The game executable");
        if (fact.SessionId == 0) return new GuardVerdict(false, "Session 0 process");

        var image = fact.Name;
        if (image.Equals("System", StringComparison.OrdinalIgnoreCase)
            || image.Equals("Registry", StringComparison.OrdinalIgnoreCase)
            || image.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            return new GuardVerdict(false, "System process");

        if (IsAntiCheat(image)) return new GuardVerdict(false, "Anti-cheat or DRM");

        foreach (var shell in BackgroundCatalog.ShellFamily)
            if (image.Equals(shell, StringComparison.OrdinalIgnoreCase))
                return new GuardVerdict(false, "Shell, compositor, audio or servicing process");

        lock (_gate)
        {
            if (_servicePids.Contains(fact.Pid)) return new GuardVerdict(false, "Hosts a Windows service");
        }

        if (settings.NeverTouch.Any(n => MatchesName(n, image)))
            return new GuardVerdict(false, "On your Never touch list");

        if (!string.IsNullOrEmpty(fact.ImagePath))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows)
                && fact.ImagePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                && !fact.ImagePath.StartsWith(Path.Combine(windows, "SystemApps"), StringComparison.OrdinalIgnoreCase))
                return new GuardVerdict(false, "Image under the Windows folder");
        }

        if (fact.ImagePath?.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) == true)
            return new GuardVerdict(false, "Packaged app");

        var detail = Inspect(NativeMethods.Handles.OpenProcess(
            NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)fact.Pid));
        if (detail != null) return new GuardVerdict(false, detail);

        return new GuardVerdict(true, "Background app");
    }

    private string? Inspect(SafeProcessHandle handle)
    {
        if (handle.IsInvalid) return "Cannot query the process";
        try
        {
            IntPtr raw = handle.DangerousGetHandle();
            var protection = new NativeMethods.GameMode.ProtectionLevelInfo();
            if (!NativeMethods.GameMode.GetProcessInformation(raw, NativeMethods.GameMode.ProcessProtectionLevelInfo, ref protection, Marshal.SizeOf<NativeMethods.GameMode.ProtectionLevelInfo>()))
                return "Protection state unknown";
            if (protection.ProtectionLevel != 0) return "Protected process (PPL)";

            if (NativeMethods.GameMode.IsProcessCritical(raw, out bool critical) && critical)
                return "Critical system process";

            var sid = ProcessSid(raw);
            if (sid == null) return "Cannot read the process token";
            if (sid == "S-1-5-18" || sid == "S-1-5-19" || sid == "S-1-5-20")
                return "Runs as SYSTEM, LOCAL SERVICE or NETWORK SERVICE";
            if (!string.Equals(sid, _selfSid, StringComparison.OrdinalIgnoreCase))
                return "Belongs to another user";
            return null;
        }
        catch
        {
            return "Cannot query the process";
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static string? ProcessSid(IntPtr process)
    {
        if (!NativeMethods.GameMode.OpenProcessToken(process, 0x0008, out IntPtr token)) return null;
        try
        {
            NativeMethods.GameMode.GetTokenInformation(token, 1, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!NativeMethods.GameMode.GetTokenInformation(token, 1, buffer, needed, out _)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero) return null;
                if (!NativeMethods.GameMode.ConvertSidToStringSid(sid, out IntPtr text)) return null;
                try { return Marshal.PtrToStringUni(text); }
                finally { NativeMethods.GameMode.LocalFree(text); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            NativeMethods.Handles.CloseHandle(token);
        }
    }

    private static string CurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool MatchesName(string pattern, string imageName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var trimmed = pattern.Trim();
        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(trimmed)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(imageName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return imageName.Equals(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAntiCheat(string image)
    {
        foreach (var name in BackgroundCatalog.AntiCheatNames)
            if (image.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var token in new[] { "anticheat", "battleye", "easyanticheat", "faceit", "gameguard", "xigncode", "punkbuster", "vgc.exe", "vgk.sys", "bedaisy" })
            if (image.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IReadOnlySet<int> ExpandGameTree(IReadOnlyList<ProcessFact> facts, int gamePid)
    {
        var set = new HashSet<int> { gamePid };
        var parents = facts.ToDictionary(f => f.Pid, f => f.ParentPid);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var fact in facts)
            {
                if (set.Contains(fact.Pid)) continue;
                if (parents.TryGetValue(fact.Pid, out int parent) && set.Contains(parent))
                {
                    set.Add(fact.Pid);
                    changed = true;
                }
            }
        }
        int current = gamePid;
        for (int i = 0; i < 32 && parents.TryGetValue(current, out int parent) && parent > 4; i++)
        {
            set.Add(parent);
            current = parent;
        }
        return set;
    }

    private static HashSet<int> ReadServicePids()
    {
        var pids = new HashSet<int>();
        IntPtr manager = NativeMethods.GameMode.OpenSCManager(null, null, NativeMethods.GameMode.ScManagerEnumerateService);
        if (manager == IntPtr.Zero) return pids;
        try
        {
            int resume = 0;
            int size = 64 * 1024;
            while (true)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!NativeMethods.GameMode.EnumServicesStatusEx(
                            manager, NativeMethods.GameMode.ScStatusProcessInfo,
                            NativeMethods.GameMode.ServiceWin32, NativeMethods.GameMode.ServiceStateAll,
                            buffer, size, out int needed, out int returned, ref resume, null))
                    {
                        if (needed > size)
                        {
                            size = needed;
                            continue;
                        }
                        break;
                    }

                    int structSize = Marshal.SizeOf<NativeMethods.GameMode.EnumServiceStatusProcess>();
                    for (int i = 0; i < returned; i++)
                    {
                        var item = Marshal.PtrToStructure<NativeMethods.GameMode.EnumServiceStatusProcess>(buffer + i * structSize);
                        if (item.Status.ProcessId > 4) pids.Add((int)item.Status.ProcessId);
                    }
                    if (resume == 0) break;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch
        {
        }
        finally
        {
            NativeMethods.GameMode.CloseServiceHandle(manager);
        }
        return pids;
    }
}
