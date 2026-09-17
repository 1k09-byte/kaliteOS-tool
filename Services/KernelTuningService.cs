using Microsoft.Win32;
using stellarisKIT.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace stellarisKIT.Services;

/// <summary>
/// Applies the kernel timer/interrupt tweaks (backed by the supplied .reg logic)
/// and detects their current state. Per-scheme values are resolved against the
/// ACTIVE power scheme — never a hardcoded GUID.
/// </summary>
public sealed class KernelTuningService
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
        bool NeedsReboot);

    public static readonly IReadOnlyList<TweakDef> All = new[]
    {
        new TweakDef(
            "ThreadedDpc",
            "Threaded DPCs",
            "Runs deferred procedure calls on dedicated threads (1) instead of inline at DISPATCH_LEVEL (0). Can smooth DPC latency spikes on some systems.",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
            "ThreadedDpcEnable", 1, 0, false, true),
        new TweakDef(
            "InterruptRouting",
            "Disable automatic interrupt routing",
            "Stops Windows from automatically steering device interrupts across CPU cores for the active power scheme. Pairs with manual IRQ affinity in Affinity Tuning.",
            @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{scheme}",
            "DisableInterruptRouting", 1, 0, true, false),
        new TweakDef(
            "TimerExpiration",
            "Serialized timer expiration",
            "Serializes timer expiration for the active power scheme. Can reduce timer coalescing jitter at the cost of throughput.",
            @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{scheme}",
            "SerializeTimerExpiration", 1, 0, true, false),
    };

    public static TweakDef? Find(string id)
    {
        foreach (var d in All)
            if (d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
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
    /// of having written it (an external .reg import shows as such).</summary>
    public TweakDetection Detect(TweakDef def)
    {
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
    // kaliteConfig\Backup so it can be restored exactly — including restoring
    // "not set" — via ResetToOriginal. Subsequent writes don't overwrite the
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

    /// <summary>Restores the value snapshotted before the first write — including
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
        try
        {
            EnsureBackup(def); // snapshot the original state before the first write
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(ResolveKey(def), true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(def.ValueName, enable ? def.OnValue : def.OffValue, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Administrator rights are required to change kernel tweaks.");
        }
    }

    // ---- Win32PrioritySeparation (foreground/background scheduling quanta) ----
    public const string PriorityControlKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    public const string Win32PSValue = "Win32PrioritySeparation";

    public static readonly (uint Value, string Label)[] Win32PSPresets = new[]
    {
        (0x02u, "2 (0x02) — Windows default"),
        (0x26u, "38 (0x26) — Balanced workstation"),
        (0x28u, "40 (0x28) — Gaming / foreground responsiveness"),
        (0x2Au, "42 (0x2A) — Maximum foreground boost"),
        (0x18u, "24 (0x18) — Background services / server-like"),
    };

    /// <summary>Current value, or null when absent (Windows default behavior).</summary>
    public uint? ReadWin32PS()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(PriorityControlKey, false);
        return key?.GetValue(Win32PSValue) switch
        {
            int i => (uint)i,
            long l => (uint)l,
            _ => null,
        };
    }

    /// <summary>Writes the value. Takes effect after a restart.</summary>
    public void WriteWin32PS(uint value)
    {
        try
        {
            // Same protection as the kernel toggles: snapshot before first write.
            EnsureBackup(new TweakDef("Win32PrioritySeparation", "", "", PriorityControlKey, Win32PSValue, 0, 0, false, false));
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(PriorityControlKey, true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(Win32PSValue, value, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Administrator rights are required to change this value.");
        }
    }

    /// <summary>Deletes the value so Windows falls back to its built-in default.</summary>
    public void Reset(TweakDef def)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ResolveKey(def), true);
            key?.DeleteValue(def.ValueName, false);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Administrator rights are required to change kernel tweaks.");
        }
    }

    // ---- SvcHostSplitThresholdInKB (service host splitting RAM threshold) ----
    // Above this RAM size Windows stops co-splitting services into shared
    // svchost instances. Values from the common guide, in KB (hex):
    public const string SvcSplitKey = @"SYSTEM\CurrentControlSet\Control";
    public const string SvcSplitValue = "SvcHostSplitThresholdInKB";

    public static readonly (uint Value, string Label)[] SvcSplitPresets = new[]
    {
        (380000u,     "(default) — 380000 KB"),
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
        try
        {
            // Same protection as the kernel toggles: snapshot before first write.
            EnsureBackup(new TweakDef("SvcHostSplitThresholdInKB", "", "", SvcSplitKey, SvcSplitValue, 0, 0, false, false));
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(SvcSplitKey, true);
            if (key == null) throw new InvalidOperationException("Could not open the registry key.");
            key.SetValue(SvcSplitValue, valueKb, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Administrator rights are required to change this value.");
        }
    }

    /// <summary>Deletes the value, restoring the Windows default (~380000 KB).</summary>
    public void ResetSvcSplit()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SvcSplitKey, true);
            key?.DeleteValue(SvcSplitValue, false);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Administrator rights are required to change this value.");
        }
    }
}
