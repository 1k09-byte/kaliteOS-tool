using System;
using System.Globalization;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize.Native;

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

    public static bool HasAnyValue(string subKey)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null && key.GetValueNames().Length > 0;
    }

    public static bool SameValue(object? actual, object wanted) =>
        actual is not null &&
        string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
                      Convert.ToString(wanted, CultureInfo.InvariantCulture),
                      StringComparison.Ordinal);
}
