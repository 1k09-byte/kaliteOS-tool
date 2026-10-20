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

public sealed class WindowsSettingsService
{
    public sealed record TweakDef(
        string Id,
        string Name,
        string Description,
        string SubKey,
        string ValueName,
        uint OnValue,
        uint OffValue,
        bool PerActiveScheme,
        bool NeedsReboot,
        Guid PowerSubgroup = default,
        Guid PowerSetting = default)
    {
        public bool IsPowerSetting => PowerSubgroup != Guid.Empty && PowerSetting != Guid.Empty;
    }
    public static readonly IReadOnlyList<TweakDef> All = new[]
    {
        new TweakDef(
            "TimerExpiration",
            "Serialized timer expiration",
            "Forces timer serialization on (1) instead of letting the kernel decide. Read by the kernel at boot - takes effect after a restart. Can reduce timer coalescing jitter at the cost of throughput.",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "SerializeTimerExpiration", 1, 0, false, true),
        new TweakDef(
            "MmcssStatus",
            "Multimedia Class Scheduler (MMCSS)",
            "MMCSS boosts the scheduling priority of audio and video threads. Turning it OFF stops the service, which can reduce scheduling noise and DPC latency but may break some audio drivers. Turning it back ON restores the Windows default. The matching System Responsiveness value is set to 30 when ON and 100 when OFF. Takes effect after a restart.",
            @"SYSTEM\CurrentControlSet\Services\MMCSS",
            "Start", 2, 4, false, true),
    };

    public const string MmcssTweakId = "MmcssStatus";
    public const string SystemResponsivenessKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    public const string SystemResponsivenessValueName = "SystemResponsiveness";

    public const uint SystemResponsivenessWhenEnabled = 30;
    public const uint SystemResponsivenessWhenDisabled = 100;

    private static readonly TweakDef SystemResponsivenessDef = new(
        "SystemResponsiveness",
        "System responsiveness",
        "Percentage of the CPU reserved for MMCSS multimedia work.",
        SystemResponsivenessKey,
        SystemResponsivenessValueName,
        SystemResponsivenessWhenEnabled, SystemResponsivenessWhenDisabled,
        false, false);

    public static TweakDef? Find(string id)
    {
        foreach (var d in All)
            if (d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

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

    public enum TweakState { WindowsDefault, On, Off, Custom }

    public readonly record struct TweakDetection(TweakState State, uint? RawValue, bool WrittenByApp)
    {
        public string Describe(string caption) => caption;
    }

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

    public const string BackupKeyPath = @"SOFTWARE\kaliteConfig\Backup";

    private void EnsureBackup(TweakDef def)
    {
        try
        {
            string resolved = ResolveKey(def);
            string id = def.Id;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.CreateSubKey($"{BackupKeyPath}\\{id}", true);
            if (backup == null || backup.GetValue("KeyPath") is string) return;

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
        catch {  }
    }

    public bool ResetToOriginal(TweakDef def)
    {
        bool ok = ResetToOriginalCore(def);
        if (def.Id.Equals(MmcssTweakId, StringComparison.OrdinalIgnoreCase))
            ResetToOriginalCore(SystemResponsivenessDef);
        return ok;
    }

    private bool ResetToOriginalCore(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.OpenSubKey($"{BackupKeyPath}\\{def.Id}", false);
            if (backup == null) return false;
            
            bool existed = (backup.GetValue("Existed") as int?) == 1;

            if (def.IsPowerSetting)
            {
                if (!existed)
                {
                    Guid scheme = GetActiveScheme();
                    if (scheme == Guid.Empty) return false;
                    Guid sub = def.PowerSubgroup, set = def.PowerSetting;
                    return PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0) == 0
                        && PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 0) == 0;
                }
                
                int ac = (backup.GetValue("OriginalAC") as int?) ?? 0;
                int dc = (backup.GetValue("OriginalDC") as int?) ?? 0;
                Guid schemeAcDc = GetActiveScheme();
                if (schemeAcDc == Guid.Empty) return false;
                Guid subAcDc = def.PowerSubgroup, setAcDc = def.PowerSetting;
                return PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref schemeAcDc, ref subAcDc, ref setAcDc, (uint)Math.Max(0, ac)) == 0
                    && PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref schemeAcDc, ref subAcDc, ref setAcDc, (uint)Math.Max(0, dc)) == 0;
            }

            if (!existed)
            {
                using var key = baseKey.OpenSubKey(ResolveKey(def), true);
                key?.DeleteValue(def.ValueName, false);
                return true;
            }

            int original = (backup.GetValue("Original") as int?) ?? 0;

            using var keyToWrite = baseKey.CreateSubKey(ResolveKey(def), true);
            keyToWrite?.SetValue(def.ValueName, original, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

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
            EnsureBackup(def);
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(ResolveKey(def), true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(def.ValueName, enable ? def.OnValue : def.OffValue, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(def, ex);
        }

        if (def.Id.Equals(MmcssTweakId, StringComparison.OrdinalIgnoreCase))
            ApplySystemResponsivenessForMmcss(enable);
    }

    public uint? ReadSystemResponsiveness() => ReadRaw(SystemResponsivenessDef);

    public void WriteSystemResponsiveness(uint value)
    {
        try
        {
            EnsureBackup(SystemResponsivenessDef);
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(SystemResponsivenessKey, true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(SystemResponsivenessValueName, value, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(SystemResponsivenessDef, ex);
        }
    }

    private void ApplySystemResponsivenessForMmcss(bool enable) =>
        WriteSystemResponsiveness(enable ? SystemResponsivenessWhenEnabled : SystemResponsivenessWhenDisabled);

    private void ResetSystemResponsivenessForMmcss()
    {
        if (HasBackup(SystemResponsivenessDef))
            ResetToOriginalCore(SystemResponsivenessDef);
    }

    private void WritePowerSetting(TweakDef def, bool enable)
    {
        uint index = enable ? def.OnValue : def.OffValue;
        try
        {
            EnsurePowerBackup(def);
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

    private void EnsurePowerBackup(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var backup = baseKey.CreateSubKey($"{BackupKeyPath}\\{def.Id}", true);
            if (backup == null || backup.GetValue("KeyPath") is string) return;
            backup.SetValue("KeyPath", $"power:{def.PowerSubgroup:D}/{def.PowerSetting:D}");
            backup.SetValue("ValueName", "AC+DC index");
            backup.SetValue("OriginalAC", (int)(ReadPowerIndex(def, dc: false) ?? 0), RegistryValueKind.DWord);
            backup.SetValue("OriginalDC", (int)(ReadPowerIndex(def, dc: true) ?? 0), RegistryValueKind.DWord);
            backup.SetValue("Existed", 1, RegistryValueKind.DWord);
        }
        catch {  }
    }

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

        if (def.Id.Equals(MmcssTweakId, StringComparison.OrdinalIgnoreCase))
            ResetSystemResponsivenessForMmcss();
    }

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
