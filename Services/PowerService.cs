using Microsoft.Win32;
using stellarisKIT.Models;
using stellarisKIT.Native;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace stellarisKIT.Services;

public sealed class PowerService
{
    private const string PowerSettingsPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

    public void UnhideAllSettings()
    {
        try
        {
        // powercfg -attributes SUB_ALL SETTING_ALL -ATTRIB_HIDE ensures nothing is hidden
        // by traversing all subgroups and settings and clearing the Attributes DWORD.
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var powerKey = baseKey.OpenSubKey(PowerSettingsPath, true);
        if (powerKey == null) return;

        foreach (var subgroupId in powerKey.GetSubKeyNames())
        {
            using var subgroupKey = powerKey.OpenSubKey(subgroupId, true);
            if (subgroupKey == null) continue;
            
            // Unhide subgroup itself
            if (subgroupKey.GetValue("Attributes") != null)
                subgroupKey.SetValue("Attributes", 0, RegistryValueKind.DWord);

            foreach (var settingId in subgroupKey.GetSubKeyNames())
            {
                using var settingKey = subgroupKey.OpenSubKey(settingId, true);
                if (settingKey == null) continue;
                
                // Unhide setting
                try
                {
                    if (settingKey.GetValue("Attributes") != null)
                        settingKey.SetValue("Attributes", 0, RegistryValueKind.DWord);
                }
                catch { } // per-key ACL failures must not abort the sweep
            }
        }
        }
        catch { } // not elevated / key read-only: enumeration still works, settings just stay hidden
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
        progress?.Report($"Found {guids.Count} power schemes — reading settings…");
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
        uint index = 0;
        const uint MaxChoices = 512;
        bool anyChoice = false;
        Guid settingGuid = setting.Id;
        while (index < MaxChoices)
        {
            uint type;
            uint bufferSize = 0;
            uint res = PowrProf.PowerReadPossibleValue(IntPtr.Zero, ref subgroupGuid, ref settingGuid, out type, index, IntPtr.Zero, ref bufferSize);
            if (res == 259) break; // ERROR_NO_MORE_ITEMS
            if (res != 0 && res != 234) break; // not an enumerated setting (range/boolean) — stop
            var choice = new PowerSettingChoice
            {
                ValueIndex = index,
                Name = GetPossibleFriendlyName(subgroupGuid, settingGuid, index)
            };
            if (!string.IsNullOrEmpty(choice.Name))
            {
                anyChoice = true;
                setting.PossibleChoices.Add(choice);
            }
            index++;
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
        PowrProf.PowerReadPossibleFriendlyName(IntPtr.Zero, ref subgroup, ref setting, index, IntPtr.Zero, ref bufferSize);
        if (bufferSize == 0) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (PowrProf.PowerReadPossibleFriendlyName(IntPtr.Zero, ref subgroup, ref setting, index, buffer, ref bufferSize) == 0)
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Apply operations
    public void SetActiveScheme(Guid schemeGuid)
    {
        PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
    }
    
    public void DeleteScheme(Guid schemeGuid)
    {
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
        PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, val);
    }

    public void WriteDCValue(Guid scheme, Guid subgroup, Guid setting, uint val)
    {
        PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, val);
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
