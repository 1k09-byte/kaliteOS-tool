using System;
using System.Collections.Generic;
using System.Globalization;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

public sealed record ThemeOptions(bool AppsUseLight, bool SystemUsesLight, bool Transparency, bool TitleBarAccent);

public interface IThemeSettingsService
{
    ThemeOptions Current();
    IReadOnlyList<SettingResult> Apply(ThemeOptions options);
    IReadOnlyList<SettingResult> RevertSection();
    IReadOnlyList<SettingResult> ResetToWindowsDefaults();
}

public sealed class ThemeSettingsService : IThemeSettingsService
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private readonly SettingApplier _applier;

    public ThemeSettingsService(SettingApplier applier) => _applier = applier;

    public ThemeOptions Current() => new(
        ReadFlag(PersonalizeKey, "AppsUseLightTheme", true),
        ReadFlag(PersonalizeKey, "SystemUsesLightTheme", true),
        ReadFlag(PersonalizeKey, "EnableTransparency", true),
        ReadFlag(DwmKey, "ColorPrevalence", false));

    public IReadOnlyList<SettingResult> Apply(ThemeOptions o) =>
        _applier.Apply(new[]
        {
            Def("theme.apps", PersonalizeKey, "AppsUseLightTheme", o.AppsUseLight, SyncTheme),
            Def("theme.system", PersonalizeKey, "SystemUsesLightTheme", o.SystemUsesLight, SyncTheme),
            Def("theme.transparency", PersonalizeKey, "EnableTransparency", o.Transparency, SyncTheme),
            Def("theme.titlebar", DwmKey, "ColorPrevalence", o.TitleBarAccent, SyncDwm),
        });

    public IReadOnlyList<SettingResult> RevertSection() =>
        _applier.RevertWhere(
            e => e.Id.StartsWith("theme.", StringComparison.Ordinal),
            new (string, Func<bool>)[]
            {
                ("theme", SyncTheme),
                ("dwm", SyncDwm),
            });

    public IReadOnlyList<SettingResult> ResetToWindowsDefaults() =>
        Apply(new ThemeOptions(AppsUseLight: true, SystemUsesLight: true, Transparency: true, TitleBarAccent: false));

    private static SettingDef Def(string id, string subKey, string name, bool value, Func<bool> sync) =>
        new(id, subKey, name, RegistryValueKind.DWord, value ? 1 : 0, sync);

    private static bool ReadFlag(string subKey, string name, bool fallback)
    {
        var e = HkcuRegistry.ReadEntry(subKey, name);
        if (!e.Exists) return fallback;
        var text = Convert.ToString(e.Data, CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v != 0 : fallback;
    }

    private static bool SyncTheme() => SystemNotifier.BroadcastSettingChange(ImmersiveColorSet);

    private static bool SyncDwm() => SystemNotifier.BroadcastDwmColorChanged();
}
