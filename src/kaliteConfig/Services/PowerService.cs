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
using kaliteConfig.Models;
using kaliteConfig.Native;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kaliteConfig.Services;

public sealed class PowerService
{
    private const string PowerSettingsPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

    public void UnhideAllSettings()
    {
        try
        {
        // Clears ONLY the hidden bit (bit 0) of Attributes. Other bits carry
        // OEM/platform meaning on laptops (Modern Standby overlays) - the old
        // code overwrote the whole DWORD with 0 and broke Control Panel
        // ("power plan information isn't available", empty Advanced list).
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var powerKey = baseKey.OpenSubKey(PowerSettingsPath, true);
        if (powerKey == null) return;

        foreach (var subgroupId in powerKey.GetSubKeyNames())
        {
            using var subgroupKey = powerKey.OpenSubKey(subgroupId, true);
            if (subgroupKey == null) continue;

            ClearHiddenBit(subgroupKey);

            foreach (var settingId in subgroupKey.GetSubKeyNames())
            {
                using var settingKey = subgroupKey.OpenSubKey(settingId, true);
                if (settingKey == null) continue;
                try { ClearHiddenBit(settingKey); }
                catch { } // per-key ACL failures must not abort the sweep
            }
        }
        }
        catch { } // not elevated / key read-only: enumeration still works, settings just stay hidden
    }

    private static void ClearHiddenBit(RegistryKey key)
    {
        // On modern Windows (Connected Standby), settings aren't just hidden by bit 0, 
        // they are completely suppressed unless Attributes is explicitly forced to 2.
        key.SetValue("Attributes", 2, RegistryValueKind.DWord);
    }

    public ObservableCollection<PowerScheme> GetAllSchemes(IProgress<string>? progress = null)
    {
        var schemes = new ObservableCollection<PowerScheme>();
        
        Guid activeSchemeGuid = Guid.Empty;
        if (PowrProf.PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeSchemePtr) == 0 && activeSchemePtr != IntPtr.Zero)
        {
            try { activeSchemeGuid = Marshal.PtrToStructure<Guid>(activeSchemePtr); }
            finally { PowrProf.LocalFree(activeSchemePtr); }
        }

        uint index = 0;
        uint bufferSize = 16;
        var guids = new System.Collections.Generic.List<Guid>();
        while (PowrProf.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, PowrProf.ACCESS_SCHEME, index, out Guid schemeGuid, ref bufferSize) == 0)
        {
            guids.Add(schemeGuid);
            index++;
            bufferSize = 16;
        }
        progress?.Report($"Found {guids.Count} power schemes - reading settings…");
        for (int i = 0; i < guids.Count; i++)
        {
            var guid = guids[i];
            progress?.Report($"Reading scheme {i + 1} of {guids.Count}…");
            var scheme = new PowerScheme
            {
                Id = guid,
                Name = GetSchemeFriendlyName(guid),
                Description = GetSchemeDescription(guid),
                IsActive = activeSchemeGuid == guid
            };
            if (string.IsNullOrWhiteSpace(scheme.Name)) scheme.Name = guid.ToString();
            
            PopulateSubgroups(scheme);
            schemes.Add(scheme);
        }

        return schemes;
    }

    private void PopulateSubgroups(PowerScheme scheme)
    {
        uint index = 0;
        uint bufferSize = 16;
        Guid schemeGuid = scheme.Id;
        
        while (PowrProf.PowerEnumerate(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, PowrProf.ACCESS_SUBGROUP, index, out Guid subgroupGuid, ref bufferSize) == 0)
        {
            var subgroup = new PowerSubgroup
            {
                Id = subgroupGuid,
                Name = GetSubgroupFriendlyName(subgroupGuid),
                Description = GetSubgroupDescription(subgroupGuid)
            };
            if (string.IsNullOrWhiteSpace(subgroup.Name)) subgroup.Name = subgroupGuid.ToString();
            
            PopulateSettings(schemeGuid, subgroup);
            
            // Only add subgroups that actually have settings
            if (subgroup.Settings.Count > 0)
                scheme.Subgroups.Add(subgroup);
                
            index++;
            bufferSize = 16;
        }
    }

    private void PopulateSettings(Guid schemeGuid, PowerSubgroup subgroup)
    {
        uint index = 0;
        uint bufferSize = 16;
        Guid subgroupGuid = subgroup.Id;
        
        while (PowrProf.PowerEnumerate(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, PowrProf.ACCESS_INDIVIDUAL_SETTING, index, out Guid settingGuid, ref bufferSize) == 0)
        {
            var setting = new PowerSetting
            {
                Id = settingGuid,
                Name = GetSettingFriendlyName(subgroupGuid, settingGuid),
                Description = GetSettingDescription(subgroupGuid, settingGuid)
            };
            if (string.IsNullOrWhiteSpace(setting.Name)) setting.Name = settingGuid.ToString();

            // AC Value
            if (PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, out uint acValue) == 0)
                setting.AcValueIndex = acValue;
                
            // DC Value
            if (PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, out uint dcValue) == 0)
                setting.DcValueIndex = dcValue;

            PopulatePossibleValues(subgroupGuid, setting);
            
            subgroup.Settings.Add(setting);
            index++;
            bufferSize = 16;
        }
    }

    private void PopulatePossibleValues(Guid subgroupGuid, PowerSetting setting)
    {
        // Choice enumeration MUST terminate: range-type settings answer every
        // index query with an error other than NO_MORE_ITEMS, which used to spin
        // this loop forever and hang the page on "Loading".
        //
        // We probe via PowerReadPossibleFriendlyName, NOT PowerReadPossibleValue:
        // the latter raised a fatal System.ExecutionEngineException (uncatchable,
        // process-killing) even with a P/Invoke signature matching the Windows
        // SDK header, so the API is avoided entirely. A choice exists at index i
        // iff its friendly name can be read; enumerated settings have contiguous
        // indices, so the first empty read means we are done.
        uint index = 0;
        const uint MaxChoices = 512;
        bool anyChoice = false;
        Guid settingGuid = setting.Id;
        try
        {
            while (index < MaxChoices)
            {
                string name = GetPossibleFriendlyName(subgroupGuid, settingGuid, index);
                if (string.IsNullOrEmpty(name)) break;
                anyChoice = true;
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = index, Name = name });
                index++;
            }
        }
        catch { } // a native failure here must never take the page down

        // Fallback for known settings where the OS fails to report possible values via API
        if (!anyChoice)
        {
            var lowerName = (setting.Name ?? "").ToLowerInvariant();

            if (lowerName.Contains("hipm/dipm"))
            {
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 0, Name = "Active" });
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 1, Name = "HIPM" });
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 2, Name = "DIPM" });
                anyChoice = true;
            }
            else if (lowerName.Contains("usb 3 link"))
            {
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 0, Name = "Off" });
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 1, Name = "Minimum power savings" });
                setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 2, Name = "Maximum power savings" });
                anyChoice = true;
            }
            
            if (!anyChoice)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroupGuid}\{settingGuid}");
                    if (key != null)
                    {
                        var maxObj = key.GetValue("ValueMax");
                        if (maxObj != null && Convert.ToUInt32(maxObj) == 1)
                        {
                            setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 0, Name = "Disabled" });
                            setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = 1, Name = "Enabled" });
                            anyChoice = true;
                        }
                    }
                }
                catch { }
            }

            if (anyChoice)
            {
                // Ensure current active values are in the combobox if not present in presets!
                var curAc = (uint)setting.AcValueIndex;
                var curDc = (uint)setting.DcValueIndex;
                
                if (!System.Linq.Enumerable.Any(setting.PossibleChoices, c => c.ValueIndex == curAc))
                    setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = curAc, Name = $"Custom ({curAc})" });
                
                if (curDc != curAc && !System.Linq.Enumerable.Any(setting.PossibleChoices, c => c.ValueIndex == curDc))
                    setting.PossibleChoices.Add(new PowerSettingChoice { ValueIndex = curDc, Name = $"Custom ({curDc})" });
            }
        }

        setting.Type = anyChoice ? 2u : 0u;
    }

    private delegate uint ReadFn(IntPtr buf, ref uint size);
    private static string AllocRead(ref uint size, ReadFn read)
    {
        if (size == 0) return string.Empty;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (read(buffer, ref size) == 0)
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string GetSchemeFriendlyName(Guid scheme)
    {
        var s = scheme; uint sz = 0;
        PowrProf.PowerReadFriendlyNameScheme(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadFriendlyNameScheme(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, b, ref z));
    }

    private string GetSchemeDescription(Guid scheme)
    {
        var s = scheme; uint sz = 0;
        PowrProf.PowerReadDescriptionScheme(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadDescriptionScheme(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, b, ref z));
    }

    private string GetSubgroupFriendlyName(Guid subgroup)
    {
        var g = subgroup; uint sz = 0;
        PowrProf.PowerReadFriendlyNameSubgroup(IntPtr.Zero, IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadFriendlyNameSubgroup(IntPtr.Zero, IntPtr.Zero, ref g, IntPtr.Zero, b, ref z));
    }

    private string GetSubgroupDescription(Guid subgroup)
    {
        var g = subgroup; uint sz = 0;
        PowrProf.PowerReadDescriptionSubgroup(IntPtr.Zero, IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadDescriptionSubgroup(IntPtr.Zero, IntPtr.Zero, ref g, IntPtr.Zero, b, ref z));
    }

    private string GetSettingFriendlyName(Guid subgroup, Guid setting)
    {
        var sg = subgroup; var st = setting; uint sz = 0;
        PowrProf.PowerReadFriendlyNameSetting(IntPtr.Zero, IntPtr.Zero, ref sg, ref st, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadFriendlyNameSetting(IntPtr.Zero, IntPtr.Zero, ref sg, ref st, b, ref z));
    }

    private string GetSettingDescription(Guid subgroup, Guid setting)
    {
        var sg = subgroup; var st = setting; uint sz = 0;
        PowrProf.PowerReadDescriptionSetting(IntPtr.Zero, IntPtr.Zero, ref sg, ref st, IntPtr.Zero, ref sz);
        return AllocRead(ref sz, (IntPtr b, ref uint z) => PowrProf.PowerReadDescriptionSetting(IntPtr.Zero, IntPtr.Zero, ref sg, ref st, b, ref z));
    }

    private string GetPossibleFriendlyName(Guid subgroup, Guid setting, uint index)
    {
        uint bufferSize = 0;
        PowrProf.PowerReadPossibleFriendlyName(IntPtr.Zero, IntPtr.Zero, ref subgroup, ref setting, index, IntPtr.Zero, ref bufferSize);
        if (bufferSize == 0) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PowrProf.PowerReadPossibleFriendlyName(IntPtr.Zero, IntPtr.Zero, ref subgroup, ref setting, index, buffer, ref bufferSize) == 0)
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Apply operations - native return codes are checked so failures surface
    // in the UI instead of silently doing nothing.
    public void SetActiveScheme(Guid schemeGuid)
    {
        uint res = PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
        if (res != 0)
            throw new InvalidOperationException($"PowerSetActiveScheme failed (Win32 error {res}). Try running the app as administrator.");
    }
    
    public static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static bool IsBuiltInScheme(Guid id) =>
        id == BalancedGuid || id == HighPerformanceGuid || id == PowerSaverGuid || id == UltimatePerformanceGuid;

    /// <summary>Restores Windows default schemes (recovery for broken CPL:
    /// "power plan information isn't available"). Requires elevation.</summary>
    public static void RestoreDefaultSchemes()
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg", "/restoredefaultschemes")
        {
            CreateNoWindow = true, UseShellExecute = false
        });
        p?.WaitForExit(30000);
    }

    public void DeleteScheme(Guid schemeGuid)
    {
        if (IsBuiltInScheme(schemeGuid))
            throw new InvalidOperationException("Built-in Windows plans (Balanced / High performance / Power saver) cannot be deleted - duplicate one instead. Deleting them breaks Control Panel on laptops.");
        PowrProf.PowerDeleteScheme(IntPtr.Zero, ref schemeGuid);
    }

    public Guid DuplicateScheme(Guid sourceScheme)
    {
        if (PowrProf.PowerDuplicateScheme(IntPtr.Zero, ref sourceScheme, out IntPtr destPtr) == 0)
        {
            try { return Marshal.PtrToStructure<Guid>(destPtr); }
            finally { PowrProf.LocalFree(destPtr); }
        }
        throw new InvalidOperationException("Failed to duplicate power scheme.");
    }
    
    public void WriteACValue(Guid scheme, Guid subgroup, Guid setting, uint val)
    {
        uint res = PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, val);
        if (res != 0)
            throw new InvalidOperationException($"PowerWriteACValueIndex failed (Win32 error {res}).");
    }

    public void WriteDCValue(Guid scheme, Guid subgroup, Guid setting, uint val)
    {
        uint res = PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, val);
        if (res != 0)
            throw new InvalidOperationException($"PowerWriteDCValueIndex failed (Win32 error {res}).");
    }

    public void WritePlanName(Guid scheme, string name)
    {
        var bytes = Encoding.Unicode.GetBytes(name + "\0");
        PowrProf.PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length);
    }

    public void WritePlanDescription(Guid scheme, string desc)
    {
        var bytes = Encoding.Unicode.GetBytes(desc + "\0");
        PowrProf.PowerWriteDescription(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length);
    }
}
