using Microsoft.Win32;
using System;
using System.Security;

namespace kaliteConfig.Services;

public sealed record HibernateStatus(bool Enabled, string? Error);

public sealed class HibernateSettingService
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public HibernateStatus Read()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(PowerKey, false);
            if (key is null) return new HibernateStatus(false, "Can't read the power settings key.");
            object? value = key.GetValue("HibernateEnabled");
            if (value is not int enabled) return new HibernateStatus(false, "HibernateEnabled is missing.");
            return new HibernateStatus(enabled != 0, null);
        }
        catch (Exception ex) { return new HibernateStatus(false, ex.Message); }
    }

    public string? Write(bool enabled)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(PowerKey, true);
            if (key is null) return "Could not open the power settings key for writing.";
            int value = enabled ? 1 : 0;
            key.SetValue("AllowHibernate", value, RegistryValueKind.DWord);
            key.SetValue("HibernateEnabled", value, RegistryValueKind.DWord);
            key.SetValue("HibernateEnabledDefault", value, RegistryValueKind.DWord);
            key.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
            object? readBack = key.GetValue("HibernateEnabled");
            if (readBack is not int check || (check != 0) != enabled)
                return "The value was written but reading it back did not match. Hibernation may be controlled by policy.";
            return null;
        }
        catch (UnauthorizedAccessException) { return "Administrator rights are required. Restart kaliteConfig as administrator."; }
        catch (SecurityException) { return "Administrator rights are required. Restart kaliteConfig as administrator."; }
        catch (Exception ex) { return ex.Message; }
    }
}
