using System;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

// One HKCU value the Personalize feature is allowed to change.
// Value is an int for DWORD and a string for REG_SZ.
// LiveSync reads the registry state for its area and pushes it to Windows
// (SystemParametersInfo or a broadcast). The applier calls it after the write
// and again after a rollback, so the live state always matches the registry.
public sealed record SettingDef(
    string Id,
    string SubKey,
    string Name,
    RegistryValueKind Kind,
    object Value,
    Func<bool>? LiveSync = null);

public sealed record SettingResult(string Id, bool Ok, string Message);
