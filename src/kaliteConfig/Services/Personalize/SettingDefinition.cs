using System;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

public sealed record SettingDef(
    string Id,
    string SubKey,
    string Name,
    RegistryValueKind Kind,
    object Value,
    Func<bool>? LiveSync = null);

public sealed record SettingResult(string Id, bool Ok, string Message);
