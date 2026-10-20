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
    IReadOnlyList<SettingResult> ApplyAccentColor(int argb);
    IReadOnlyList<SettingResult> RevertSection();
    IReadOnlyList<SettingResult> ResetToWindowsDefaults();
}

public sealed class ThemeSettingsService : IThemeSettingsService
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string DwmKey = @"Software\Microsoft\Windows\DWM";
    public const string ExplorerAccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private const string ImmersiveColorSet = "ImmersiveColorSet";

    public IReadOnlyList<SettingResult> ApplyAccentColor(int argb)
    {
        int abgr = unchecked((int)((((uint)argb & 0x00FFFFFF) << 8) | 0x000000FF));
        return _applier.Apply(new[]
        {
            new SettingDef("theme.accent.dwm", DwmKey, "AccentColor", RegistryValueKind.DWord, abgr, SyncTheme),
            new SettingDef("theme.accent.colorize", DwmKey, "ColorizationColor", RegistryValueKind.DWord, abgr, SyncTheme),
            new SettingDef("theme.accent.afterglow", DwmKey, "ColorizationAfterglow", RegistryValueKind.DWord, abgr, SyncTheme),
            new SettingDef("theme.accent.menu", ExplorerAccentKey, "AccentColorMenu", RegistryValueKind.DWord, unchecked((int)(((uint)argb) | 0xFF000000)), SyncTheme),
            new SettingDef("theme.accent.palette", ExplorerAccentKey, "AccentPalette", RegistryValueKind.Binary, BuildPalette(abgr), SyncTheme),
        });
    }

    private static byte[] BuildPalette(int abgr)
    {
        byte a = (byte)((uint)abgr >> 24);
        byte r = (byte)((uint)abgr >> 16);
        byte g = (byte)((uint)abgr >> 8);
        byte b = (byte)(uint)abgr;

        var tints = new List<byte>();
        foreach (double t in new[] { 0.30, 0.52, 0.74, 0.96 })
        {
            tints.Add(Mix(r, t));
            tints.Add(Mix(g, t));
            tints.Add(Mix(b, t));
        }

        tints.Add(r);
        tints.Add(g);
        tints.Add(b);

        foreach (double s in new[] { 0.16, 0.32, 0.48, 0.64 })
        {
            tints.Add(Dim(r, s));
            tints.Add(Dim(g, s));
            tints.Add(Dim(b, s));
        }

        var all = new byte[32];
        all[0] = a;
        for (int i = 0; i < tints.Count && i + 1 < all.Length; i++) all[i + 1] = tints[i];
        return all;
    }

    private static byte Mix(byte channel, double t) =>
        (byte)Math.Clamp(Math.Round(channel + (255 - channel) * t), 0, 255);

    private static byte Dim(byte channel, double t) =>
        (byte)Math.Clamp(Math.Round(channel * (1.0 - t)), 0, 255);

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
