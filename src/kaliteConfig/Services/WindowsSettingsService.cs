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
using Microsoft.Win32;
using kaliteConfig.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

/// <summary>
/// Applies the kernel timer/interrupt tweaks (backed by the supplied .reg logic)
/// and detects their current state. Per-scheme values are resolved against the
/// ACTIVE power scheme - never a hardcoded GUID.
/// </summary>
public sealed class WindowsSettingsService
{
    public sealed record TweakDef(
        string Id,
        string Name,
        string Description,
        string SubKey,      // "{scheme}" is replaced with the active scheme GUID when PerActiveScheme
        string ValueName,
        uint OnValue,
        uint OffValue,
        bool PerActiveScheme,
        bool NeedsReboot,
        // Power-setting mechanism (InterruptRouting only): when set, the
        // tweak is a real power-scheme setting written via PowrProf
        // AC+DC value indices - NOT a direct DWORD. OnValue/OffValue double
        // as the on/off indices so detection and the UI keep working unchanged.
        Guid PowerSubgroup = default,
        Guid PowerSetting = default)
    {
        /// <summary>True when this tweak is a power-scheme setting (PowrProf path).</summary>
        public bool IsPowerSetting => PowerSubgroup != Guid.Empty && PowerSetting != Guid.Empty;
    }

    // Interrupt Steering: subgroup + setting GUIDs (documented power schema).
    private static readonly Guid IntSteerSubgroup = new("48672f38-7a9a-4bb2-8bf8-3d85be19de4e");
    private static readonly Guid IntSteerModeSetting = new("2bfc24f9-5ea2-4801-8213-3dbae01aa39d");

    /// <summary>Interrupt Steering Mode index 4 = "Lock Interrupt Routing".</summary>
    private const uint IntSteerLockIndex = 4;

    /// <summary>Interrupt Steering Mode index 0 = "Default".</summary>
    private const uint IntSteerDefaultIndex = 0;

    public static readonly IReadOnlyList<TweakDef> All = new[]
    {
        new TweakDef(
            "ThreadedDpc",
            "Threaded DPCs",
            "Runs deferred procedure calls on dedicated threads (1) instead of inline at DISPATCH_LEVEL (0). Can smooth DPC latency spikes on some systems. Read by the kernel at boot - takes effect after a restart.",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "ThreadedDpcEnable", 1, 0, false, true),
        new TweakDef(
            "InterruptRouting",
            "Lock interrupt routing",
            "Sets Interrupt Steering Mode to 'Lock Interrupt Routing' for the active power scheme, plugged in and on battery - Windows stops moving device interrupts across cores. Pairs with manual IRQ affinity in Affinity Tuning. Applies immediately.",
            "", "", IntSteerLockIndex, IntSteerDefaultIndex, true, false,
            IntSteerSubgroup, IntSteerModeSetting),
        new TweakDef(
            "TimerExpiration",
            "Serialized timer expiration",
            "Forces timer serialization on (1) instead of letting the kernel decide. Read by the kernel at boot - takes effect after a restart. Can reduce timer coalescing jitter at the cost of throughput.",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "SerializeTimerExpiration", 1, 0, false, true),
        new TweakDef(
            "MmcssStatus",
            "Disable Multimedia Class Scheduler (MMCSS)",
            "Disabling MMCSS (turning this ON) can reduce scheduling noise and DPC latency, but may break some audio drivers. Turning it OFF restores the default behavior of boosting multimedia thread priorities. Takes effect after a restart.",
            @"SYSTEM\CurrentControlSet\Services\MMCSS",
            "Start", 4, 2, false, true),
    };

    public static TweakDef? Find(string id)
    {
        foreach (var d in All)
            if (d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>
    /// True when THIS process token carries the Administrator role - the real
    /// elevation check. Never throws (false on any failure).
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds the truthful access-denied error for a fully-described write
    /// target. "Run as admin" is only claimed when the process genuinely is
    /// NOT elevated; an elevated process that is still denied gets the real
    /// target, the OS error, and the likely culprits (security software,
    /// hardened key ACLs). Pure logic - unit-tested.
    /// </summary>
    internal static string AccessDeniedMessage(string location, bool elevated, string osError)
    {
        if (!elevated)
            return "Administrator rights are required to change kernel tweaks. " +
                   "Restart kaliteConfig as administrator and try again.";
        return $"Access was denied writing {location} even though kaliteConfig IS running " +
               "as administrator. A security product (antivirus/EDR) may be blocking kernel registry writes, " +
               "or this key's permissions were changed on your system. Confirm Task Manager > Details shows " +
               $"Elevated=Yes for kaliteConfig, then retry with such protection paused. (OS error: {osError})";
    }

    /// <summary>Wraps a registry access failure with the truthful message for this tweak.</summary>
    private static UnauthorizedAccessException Denied(TweakDef def, Exception inner)
    {
        string where;
        try
        {
            where = def.IsPowerSetting
                ? $"power setting '{def.Name}' [{def.PowerSetting:D}] (AC + DC) on the active power scheme"
                : $"HKLM\\{ResolveKey(def)}\\{def.ValueName}";
        }
        catch { where = def.IsPowerSetting ? "the Interrupt Steering power setting" : $"HKLM\\{def.SubKey}\\{def.ValueName}"; }
        return new UnauthorizedAccessException(
            AccessDeniedMessage(where, IsElevated(), inner.Message), inner);
    }

    public static string ResolveKey(TweakDef def)
    {
        if (!def.PerActiveScheme) return def.SubKey;
        return def.SubKey.Replace("{scheme}", GetActiveScheme().ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    public static Guid GetActiveScheme()
    {
        if (PowrProf.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
        {
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { PowrProf.LocalFree(ptr); }
        }
        return Guid.Empty;
    }

    /// <summary>Current DWORD, or null when the value is absent (Windows default).</summary>
    public uint? ReadRaw(TweakDef def)
    {
        if (def.IsPowerSetting) return ReadPowerIndex(def, dc: false);
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(ResolveKey(def), false);
        var v = key?.GetValue(def.ValueName);
        return v switch
        {
            int i => (uint)i,
            long l => (uint)l,
            _ => null,
        };
    }

    /// <summary>
    /// Reads one Interrupt-Steering-style power index for the active scheme.
    /// Null when the scheme can't be determined or the API refuses - callers
    /// treat null as unknown, never as zero.
    /// </summary>
    private static uint? ReadPowerIndex(TweakDef def, bool dc)
    {
        try
        {
            Guid scheme = GetActiveScheme();
            if (scheme == Guid.Empty) return null;
            Guid sub = def.PowerSubgroup, set = def.PowerSetting;
            uint rc, index;
            rc = dc
                ? PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out index)
                : PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out index);
            return rc == 0 ? index : null;
        }
        catch { return null; }
    }

    /// <summary>Full state of a tweak value: what the UI detection needs to
    /// distinguish "value absent (Windows default)" from "explicitly 0/1"
    /// and to know whether the app itself ever wrote it.</summary>
    public enum TweakState { WindowsDefault, On, Off, Custom }

    public readonly record struct TweakDetection(TweakState State, uint? RawValue, bool WrittenByApp)
    {
        public string Describe(string caption) => State switch
        {
            TweakState.WindowsDefault => caption + " Currently: not set (Windows default).",
            TweakState.On => caption + " Currently: ON." + (WrittenByApp ? "" : " (set outside the app)"),
            TweakState.Off => caption + " Currently: OFF." + (WrittenByApp ? "" : " (set outside the app)"),
            _ => caption + $" Currently: custom ({RawValue}).",
        };
    }

    /// <summary>Detects the tweak's full current state, including whether the
    /// value exists at all (Windows default) and whether the app has a record
    /// of having written it (an external .reg import shows as such).
    /// Power tweaks classify on AC+DC indices: both locked = On, both
    /// default = Off, anything else (e.g. AC-only via powercfg) = Custom.</summary>
    public TweakDetection Detect(TweakDef def)
    {
        if (def.IsPowerSetting)
        {
            uint? ac = ReadPowerIndex(def, dc: false);
            uint? dc = ReadPowerIndex(def, dc: true);
            TweakState powerState = (ac, dc) switch
            {
                (null, null) => TweakState.WindowsDefault,
                (uint a, uint d) when a == def.OnValue && d == def.OnValue => TweakState.On,
                (uint a, uint d) when a == def.OffValue && d == def.OffValue => TweakState.Off,
                _ => TweakState.Custom,
            };
            return new TweakDetection(powerState, ac, HasBackup(def));
        }
        uint? raw = ReadRaw(def);
        TweakState state = raw switch
        {
            null => TweakState.WindowsDefault,
            uint v when v == def.OnValue => TweakState.On,
            uint v when v == def.OffValue => TweakState.Off,
            _ => TweakState.Custom,
        };
        bool writtenByApp = HasBackup(def);
        return new TweakDetection(state, raw, writtenByApp);
    }

    // ---- Registry value protection ----------------------------------------
    // The kernel tweaks touch sensitive keys (Session Manager\Kernel, power
    // scheme values). Before the FIRST write to a given key+value, the
    // original state (value or "absent") is snapshotted under HKLM\SOFTWARE\
    // kaliteConfig\Backup so it can be restored exactly - including restoring
    // "not set" - via ResetToOriginal. Subsequent writes don't overwrite the
    // backup, so the true original survives repeated toggling.
    public const string BackupKeyPath = @"SOFTWARE\kaliteConfig\Backup";

    private void EnsureBackup(TweakDef def)
    {
        try
        {
            string resolved = ResolveKey(def);
            string id = def.Id;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.CreateSubKey($"{BackupKeyPath}\\{id}", true);
            if (backup == null || backup.GetValue("KeyPath") is string) return; // already backed up

            backup.SetValue("KeyPath", resolved);
            backup.SetValue("ValueName", def.ValueName);
            using var key = baseKey.OpenSubKey(resolved, false);
            var v = key?.GetValue(def.ValueName);
            if (v is int i)
            {
                backup.SetValue("Original", i, RegistryValueKind.DWord);
                backup.SetValue("Existed", 1, RegistryValueKind.DWord);
            }
            else
            {
                backup.SetValue("Existed", 0, RegistryValueKind.DWord);
            }
        }
        catch { /* best-effort: protection never blocks the tweak itself */ }
    }

    /// <summary>Restores the value snapshotted before the first write - including
    /// deleting it again if it did not exist originally. Returns false when no
    /// backup exists for this tweak.</summary>
    public bool ResetToOriginal(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.OpenSubKey($"{BackupKeyPath}\\{def.Id}", false);
            if (backup == null) return false;
            if ((backup.GetValue("Existed") as int?) != 1) return false;

            if (def.IsPowerSetting)
            {
                int ac = (backup.GetValue("OriginalAC") as int?) ?? 0;
                int dc = (backup.GetValue("OriginalDC") as int?) ?? 0;
                Guid scheme = GetActiveScheme();
                if (scheme == Guid.Empty) return false;
                Guid sub = def.PowerSubgroup, set = def.PowerSetting;
                return PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)Math.Max(0, ac)) == 0
                    && PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)Math.Max(0, dc)) == 0;
            }

            int original = (backup.GetValue("Original") as int?) ?? 0;

            using var key = baseKey.CreateSubKey(ResolveKey(def), true);
            key?.SetValue(def.ValueName, original, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when a pre-write snapshot exists for this tweak.</summary>
    public bool HasBackup(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.OpenSubKey($"{BackupKeyPath}\\{def.Id}", false);
            return backup != null;
        }
        catch { return false; }
    }

    public void Write(TweakDef def, bool enable)
    {
        if (def.IsPowerSetting)
        {
            WritePowerSetting(def, enable);
            return;
        }
        try
        {
            EnsureBackup(def); // snapshot the original state before the first write
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(ResolveKey(def), true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(def.ValueName, enable ? def.OnValue : def.OffValue, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }
    }

    /// <summary>
    /// Writes an Interrupt-Steering-style power index for the active scheme,
    /// plugged in AND on battery. Win32 error 5 (access denied) flows into
    /// the truthful elevated/blocked message; any other code is reported raw.
    /// </summary>
    private void WritePowerSetting(TweakDef def, bool enable)
    {
        uint index = enable ? def.OnValue : def.OffValue;
        try
        {
            EnsurePowerBackup(def); // snapshot the original AC+DC indices before the first write
            Guid scheme = GetActiveScheme();
            if (scheme == Guid.Empty) throw new InvalidOperationException("Could not determine the active power scheme.");
            Guid sub = def.PowerSubgroup, set = def.PowerSetting;
            uint rc = PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
            if (rc != 0) throw PowerWriteFailed("AC index", rc);
            rc = PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, index);
            if (rc != 0) throw PowerWriteFailed("DC index", rc);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }
    }

    private static Exception PowerWriteFailed(string what, uint code) => code == 5
        ? new UnauthorizedAccessException($"Win32 error 5 writing {what}.")
        : new InvalidOperationException($"Could not write {what} (Win32 error {code}).");

    /// <summary>
    /// Snapshots the original AC+DC indices before the first power-setting
    /// write (counterpart of EnsureBackup for DWORD tweaks).
    /// </summary>
    private void EnsurePowerBackup(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.CreateSubKey($"{BackupKeyPath}\\{def.Id}", true);
            if (backup == null || backup.GetValue("KeyPath") is string) return; // already backed up
            backup.SetValue("KeyPath", $"power:{def.PowerSubgroup:D}/{def.PowerSetting:D}");
            backup.SetValue("ValueName", "AC+DC index");
            backup.SetValue("OriginalAC", (int)(ReadPowerIndex(def, dc: false) ?? 0), RegistryValueKind.DWord);
            backup.SetValue("OriginalDC", (int)(ReadPowerIndex(def, dc: true) ?? 0), RegistryValueKind.DWord);
            backup.SetValue("Existed", 1, RegistryValueKind.DWord);
        }
        catch { /* best-effort: protection never blocks the tweak itself */ }
    }

    /// Power tweaks have no deletable value: restores the snapshotted indices,
    /// or index 0 (Default) when nothing was ever snapshotted.</summary>
    public void Reset(TweakDef def)
    {
        if (def.IsPowerSetting)
        {
            if (!ResetToOriginal(def)) Write(def, false);
            return;
        }
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ResolveKey(def), true);
            key?.DeleteValue(def.ValueName, false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }
    }

    // ---- SvcHostSplitThresholdInKB (service host splitting RAM threshold) ----
    // Above this RAM size Windows stops co-splitting services into shared
    // svchost instances. Values from the common guide, in KB (hex):
    public const string SvcSplitKey = @"SYSTEM\CurrentControlSet\Control";
    public const string SvcSplitValue = "SvcHostSplitThresholdInKB";

    public static readonly (uint Value, string Label)[] SvcSplitPresets = new[]
    {
        (380000u,     "(default) - 380000 KB"),
        (0x400000u,   "4 GB"),
        (0x600000u,   "6 GB"),
        (0x800000u,   "8 GB"),
        (0xC00000u,   "12 GB"),
        (0x1000000u,  "16 GB"),
        (0x1800000u,  "24 GB"),
        (0x2000000u,  "32 GB"),
        (0x4000000u,  "64 GB"),
    };

    /// <summary>Current threshold in KB, or null when absent (Windows default 380000 KB).</summary>
    public uint? ReadSvcSplit()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(SvcSplitKey, false);
        return key?.GetValue(SvcSplitValue) switch
        {
            int i => (uint)i,
            long l => (uint)l,
            _ => null,
        };
    }

    public void WriteSvcSplit(uint valueKb)
    {
        var def = new TweakDef("SvcHostSplitThresholdInKB", "", "", SvcSplitKey, SvcSplitValue, 0, 0, false, false);
        try
        {
            // Same protection as the kernel toggles: snapshot before first write.
            EnsureBackup(def);
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(SvcSplitKey, true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(SvcSplitValue, valueKb, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }
    }

    /// <summary>Deletes the value, restoring the Windows default (~380000 KB).</summary>
    public void ResetSvcSplit()
    {
        var def = new TweakDef("SvcHostSplitThresholdInKB", "", "", SvcSplitKey, SvcSplitValue, 0, 0, false, false);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SvcSplitKey, true);
            key?.DeleteValue(SvcSplitValue, false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }
    }
}
