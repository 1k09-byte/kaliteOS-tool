using System;
using System.Globalization;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize.Native;

// The only registry access in the Personalize feature. It is hard-wired to
// HKEY_CURRENT_USER and throws for any attempt to reach another hive, so
// nothing here can need elevation.
internal static class HkcuRegistry
{
    public readonly record struct Entry(bool Exists, RegistryValueKind Kind, object? Data);

    private static void Guard(string subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey) ||
            subKey.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
            subKey.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Personalize may only write under HKCU.");
    }

    public static Entry ReadEntry(string subKey, string name)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null) return new Entry(false, RegistryValueKind.Unknown, null);
        var names = key.GetValueNames();
        if (Array.IndexOf(names, name) < 0) return new Entry(false, RegistryValueKind.Unknown, null);
        return new Entry(true, key.GetValueKind(name), key.GetValue(name));
    }

    public static void Write(string subKey, string name, object value, RegistryValueKind kind)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new UnauthorizedAccessException($"Cannot open HKCU\\{subKey}.");
        key.SetValue(name, value, kind);
    }

    public static void Delete(string subKey, string name)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is null) return;
        if (Array.IndexOf(key.GetValueNames(), name) >= 0) key.DeleteValue(name, throwOnMissingValue: false);
    }

    // True if a policy key exists and has any value. Used as a read-only
    // "locked by policy" hint; it never writes.
    public static bool HasAnyValue(string subKey)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null && key.GetValueNames().Length > 0;
    }

    // Compares a stored or read-back value with the wanted one. DWORDs come
    // back as int and strings as string, so both sides are compared as text.
    public static bool SameValue(object? actual, object wanted) =>
        actual is not null &&
        string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
                      Convert.ToString(wanted, CultureInfo.InvariantCulture),
                      StringComparison.Ordinal);
}
