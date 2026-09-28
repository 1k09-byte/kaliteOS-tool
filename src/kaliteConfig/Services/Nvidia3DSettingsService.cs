using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.DRS;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// One DRS row: the driver's name, its current and default value, the value labels
/// the driver publishes, and whether this editor may write it.
///
/// There is no description. The driver's own description field is the setting's name
/// repeated, so there is nothing to show that is not already on screen, and anything
/// longer would be prose this app wrote about a driver setting.
/// </summary>
public sealed record Nvidia3DRow(
    uint Id, string Name, uint Value, uint DefaultValue, bool IsInherited,
    IReadOnlyList<Nvidia3DOption> Options,
    NvidiaSettingProvenance Provenance = NvidiaSettingProvenance.Curated,
    bool IsEditable = true,
    string? ValueLabel = null)
{
    public bool DiffersFromDefault => IsInherited || Value != DefaultValue;

    public string HexId => $"0x{Id:X8}";

    /// <summary>True when the SDK vouches for this ID, so the name is the driver's own.</summary>
    public bool IsNamedByDriver => Provenance != NvidiaSettingProvenance.Unknown;
}

public sealed class Nvidia3DApplication
{
    public string Executable { get; set; }
    public string Label { get; set; }
    public string ProfileName { get; set; }
    public Nvidia3DApplication(string executable, string label, string profileName) { Executable = executable; Label = label; ProfileName = profileName; }
}

/// <summary>Per-setting result of an apply, so a partial failure names the setting and the reason.</summary>
public sealed record Nvidia3DSettingOutcome(uint Id, string Name, bool Applied, string Reason, uint? Expected, uint? Actual);

public sealed record Nvidia3DApplyResult(
    bool Success, string Message, string? BackupPath = null,
    IReadOnlyList<Nvidia3DSettingOutcome>? Outcomes = null)
{
    public IReadOnlyList<Nvidia3DSettingOutcome> OutcomeList => Outcomes ?? Array.Empty<Nvidia3DSettingOutcome>();

    public IReadOnlyList<Nvidia3DSettingOutcome> Failed => OutcomeList.Where(o => !o.Applied).ToList();
}

/// <summary>Everything both views need for the currently selected profile, from one read.</summary>
public sealed record NvidiaProfileSnapshot(
    string ProfileName,
    string ProfileKey,
    bool IsGlobal,
    IReadOnlyList<Nvidia3DRow> Simple,
    IReadOnlyList<Nvidia3DRow> AdvancedKnown,
    IReadOnlyList<Nvidia3DRow> AdvancedUnknown)
{
    public int AdvancedCount => AdvancedKnown.Count + AdvancedUnknown.Count;
}

/// <summary>Single DRS implementation used by both curated NVIDIA 3D Settings tabs.</summary>
public sealed class Nvidia3DSettingsService
{
    private readonly Dictionary<uint, uint> _staged = new();
    private static string BackupPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig", "Nvidia", "drs-backup.nvdrs");

    public IReadOnlyList<Nvidia3DDriverSetting> EnumerateDriverSettings()
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            return DRSApi.EnumAvailableSettingIds().Select(id => new Nvidia3DDriverSetting(id, SafeName(id))).ToArray();
        }
    }

    private static string SafeName(uint id)
    {
        try { return DRSApi.GetSettingNameFromId(id) ?? $"Setting 0x{id:X8}"; }
        catch { return $"Setting 0x{id:X8}"; }
    }

    public IReadOnlyList<Nvidia3DRow> ReadGlobal()
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            using var session = DriverSettingsSession.CreateAndLoad();
            return ReadRows(session, session.BaseProfile, Nvidia3DProfileScope.Global);
        }
    }

    public IReadOnlyList<Nvidia3DApplication> EnumerateApplications()
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            using var session = DriverSettingsSession.CreateAndLoad();
            return session.Profiles.SelectMany(p => p.Applications.Select(a => new Nvidia3DApplication(
                    a.ApplicationName, string.IsNullOrWhiteSpace(a.FriendlyName) ? a.ApplicationName : a.FriendlyName, p.Name)))
                .GroupBy(a => a.Executable, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public IReadOnlyList<Nvidia3DRow> ReadProgram(string executable)
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            using var session = DriverSettingsSession.CreateAndLoad();
            var app = session.FindApplication(executable);
            var profile = app?.Profile ?? session.FindApplicationProfile(executable);
            if (profile is null) return Array.Empty<Nvidia3DRow>();
            return ReadRows(session, profile, Nvidia3DProfileScope.Program);
        }
    }

    /// <summary>
    /// Reads one profile once and returns both the curated (Simple) view and the wider
    /// raw enumeration (Advanced) for that same profile, so switching the Advanced
    /// toggle never re-hits the driver and never shows a different profile's data.
    /// </summary>
    public NvidiaProfileSnapshot ReadSnapshot(bool isProgram, string? executable)
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            using var session = DriverSettingsSession.CreateAndLoad();

            if (!isProgram)
            {
                var baseProfile = session.BaseProfile;
                return BuildSnapshot(baseProfile, session, Nvidia3DProfileScope.Global);
            }

            var app = session.FindApplication(executable ?? string.Empty);
            var programProfile = app?.Profile ?? session.FindApplicationProfile(executable ?? string.Empty);
            if (programProfile is null)
            {
                return new NvidiaProfileSnapshot(
                    Path.GetFileNameWithoutExtension(executable ?? "Program"), executable ?? "program", false,
                    Array.Empty<Nvidia3DRow>(), Array.Empty<Nvidia3DRow>(), Array.Empty<Nvidia3DRow>());
            }
            return BuildSnapshot(programProfile, session, Nvidia3DProfileScope.Program);
        }
    }

    private static NvidiaProfileSnapshot BuildSnapshot(
        DriverSettingsProfile profile, DriverSettingsSession session, Nvidia3DProfileScope scope)
    {
        var simple = ReadRows(session, profile, scope);
        var (known, unknown) = ReadAdvancedRows(session, profile, scope, simple.Select(r => r.Id).ToHashSet());
        return new NvidiaProfileSnapshot(
            profile.Name,
            scope == Nvidia3DProfileScope.Global ? "global" : profile.Name,
            scope == Nvidia3DProfileScope.Global,
            simple, known, unknown);
    }

    private static IReadOnlyList<Nvidia3DRow> ReadRows(DriverSettingsSession session, DriverSettingsProfile profile, Nvidia3DProfileScope scope)
        => ReadRows(session, profile, scope, Nvidia3DSettingsCatalog.Expected);

    /// <summary>
    /// Reads the rows for one curated catalog against the live profile. The NVIDIA 3D
    /// settings page and the Simple Driver Settings page pass different catalogs, which
    /// is what keeps the same knob from appearing in both.
    /// </summary>
    private static IReadOnlyList<Nvidia3DRow> ReadRows(
        DriverSettingsSession session, DriverSettingsProfile profile, Nvidia3DProfileScope scope,
        IReadOnlyList<Nvidia3DSettingDefinition> catalog)
    {
        var global = scope == Nvidia3DProfileScope.Program ? session.BaseProfile : null;
        var driverIds = new HashSet<uint>(DRSApi.EnumAvailableSettingIds());
        return catalog
            .Where(def => (scope == Nvidia3DProfileScope.Global ? def.Global : def.Program) && driverIds.Contains(def.Id))
            .Select(def => TryReadRow(session, profile, global, scope, def))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();
    }

    /// <summary>
    /// The Simple Driver Settings view.
    ///
    /// This view is the whole driver, not a hand-written list. The rows come from
    /// <see cref="DRSApi.EnumAvailableSettingIds"/> - the settings this driver on this
    /// machine actually offers - minus the ones the NVIDIA 3D settings page already
    /// shows, so no setting is listed twice. Names, value labels and defaults are the
    /// driver's own. Nothing is described in prose because the driver publishes no
    /// prose: its KnownDescription is the setting's own name repeated, so writing
    /// anything more would be us inventing it.
    /// </summary>
    public NvidiaProfileSnapshot ReadSimpleSnapshot(bool isProgram, string? executable)
    {
        lock (NvApiSession.Gate)
        {
            NVIDIA.Initialize();
            using var session = DriverSettingsSession.CreateAndLoad();

            if (!isProgram)
                return BuildSimpleSnapshot(session.BaseProfile, session, Nvidia3DProfileScope.Global);

            var app = session.FindApplication(executable ?? string.Empty);
            var programProfile = app?.Profile ?? session.FindApplicationProfile(executable ?? string.Empty);
            if (programProfile is null)
            {
                return new NvidiaProfileSnapshot(
                    Path.GetFileNameWithoutExtension(executable ?? "Program"), executable ?? "program", false,
                    Array.Empty<Nvidia3DRow>(), Array.Empty<Nvidia3DRow>(), Array.Empty<Nvidia3DRow>());
            }
            return BuildSimpleSnapshot(programProfile, session, Nvidia3DProfileScope.Program);
        }
    }

    private static NvidiaProfileSnapshot BuildSimpleSnapshot(
        DriverSettingsProfile profile, DriverSettingsSession session, Nvidia3DProfileScope scope)
    {
        // The 3D page's settings are excluded so the same knob is never listed twice.
        var threeDPage = new HashSet<uint>(Nvidia3DSettingsCatalog.Expected.Select(d => d.Id));
        var simple = ReadDriverRows(session, profile, scope, threeDPage);

        // The Simple view already claims every setting the driver offers, so Advanced
        // has nothing left to hold. It stays empty rather than repeating rows.
        return new NvidiaProfileSnapshot(
            profile.Name,
            scope == Nvidia3DProfileScope.Global ? "global" : profile.Name,
            scope == Nvidia3DProfileScope.Global,
            simple, Array.Empty<Nvidia3DRow>(), Array.Empty<Nvidia3DRow>());
    }

    /// <summary>
    /// One row per setting the driver reports as available, minus
    /// <paramref name="excluded"/>, named and valued entirely by the driver.
    ///
    /// A profile carries settings the driver does not offer, but those are filtered
    /// out: they are leftovers, not something this machine can act on, and showing
    /// them would imply they do something. Settings the driver cannot name keep their
    /// raw hex ID, because a made-up name is worse than an honest number.
    /// </summary>
    private static IReadOnlyList<Nvidia3DRow> ReadDriverRows(
        DriverSettingsSession session, DriverSettingsProfile profile,
        Nvidia3DProfileScope scope, HashSet<uint> excluded)
    {
        var global = scope == Nvidia3DProfileScope.Program ? session.BaseProfile : null;
        List<uint> available;
        try { available = DRSApi.EnumAvailableSettingIds().Distinct().ToList(); }
        catch { return Array.Empty<Nvidia3DRow>(); }

        return available
            .Where(id => !excluded.Contains(id))
            .Select(id => TryReadDriverRow(session, profile, global, scope, id))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();
    }

    /// <summary>
    /// Reads one driver-named setting. The SDK's own name is used when it vouches for
    /// the ID and the driver's own string otherwise; both are the driver's words.
    /// </summary>
    private static Nvidia3DRow? TryReadDriverRow(
        DriverSettingsSession session, DriverSettingsProfile profile, DriverSettingsProfile? global,
        Nvidia3DProfileScope scope, uint id)
    {
        try
        {
            var settingInfo = SettingInfo.FromId(id);
            var setting = profile.GetSetting(id);
            bool inherited = scope == Nvidia3DProfileScope.Program && setting is null;
            var effective = setting?.CurrentValue;
            var inheritedValue = global?.GetSetting(id)?.CurrentValue ?? settingInfo.DefaultValue;

            // The driver names far more settings than the SDK vouches for, so the
            // driver's own string is the name of record. Provenance follows the name
            // that was actually used: a row may only claim to be unnamed when the
            // driver gave us no string for it.
            string name = SafeName(id);
            bool driverNamed = !string.IsNullOrWhiteSpace(name) && !IsHexFallback(name);
            var provenance = driverNamed
                ? NvidiaSettingProvenance.KnownAdvanced
                : NvidiaSettingProvenance.Unknown;

            var options = ReadLabelledOptions(id, settingInfo);
            bool editable = TrySettingType(settingInfo) == DRSSettingType.Integer;

            if (scope == Nvidia3DProfileScope.Program)
                options = Nvidia3DSettingsCatalog.BuildOptions(options, true, null);

            // A non-integer DRS value cannot be written by this editor, so show it
            // read-only as the driver reports it rather than coercing it to a number.
            if (!TryToUInt(effective ?? inheritedValue, out uint value)
                || !TryToUInt(settingInfo.DefaultValue, out uint defaultValue))
            {
                return new Nvidia3DRow(
                    id, name, 0, 0, inherited, options, provenance,
                    IsEditable: false, ValueLabel: DescribeRawValue(effective ?? inheritedValue));
            }

            return new Nvidia3DRow(
                id, name, value, defaultValue, inherited, options, provenance, editable);
        }
        catch { return null; }
    }

    /// <summary>
    /// True for the placeholder <see cref="SafeName"/> falls back to when the driver
    /// has no string for an ID, so a row is only called unnamed when it really is.
    /// </summary>
    private static bool IsHexFallback(string name)
        => name.StartsWith("Setting 0x", StringComparison.Ordinal);

    /// <summary>
    /// Raw per-profile enumeration, split by whether the SDK can name the setting.
    ///
    /// "Known" means the driver SDK that already ships with this app maps the ID to a
    /// known setting name and description, so we can show both. "Unknown" means we have
    /// nothing verified: the row keeps its raw hex ID and raw value, and is labelled
    /// unverified. No name is ever guessed.
    ///
    /// Only IDs the driver itself reports as available are returned. A profile
    /// carries settings that are not in the driver's available set - leftovers from
    /// an older driver, or internal state the profile happens to store. They are not
    /// settings this machine can act on, and offering them as rows the user can edit
    /// would be inventing the impression that they do something. Filtering them out
    /// here is what makes the Advanced view mean "settings this driver supports".
    /// </summary>
    private static (IReadOnlyList<Nvidia3DRow> Known, IReadOnlyList<Nvidia3DRow> Unknown) ReadAdvancedRows(
        DriverSettingsSession session, DriverSettingsProfile profile, Nvidia3DProfileScope scope, HashSet<uint> curatedIds)
    {
        var known = new List<Nvidia3DRow>();
        var unknown = new List<Nvidia3DRow>();
        var seen = new HashSet<uint>();
        HashSet<uint> available;
        try { available = new HashSet<uint>(DRSApi.EnumAvailableSettingIds()); }
        catch { return (known, unknown); }

        foreach (var setting in EnumerateProfileSettings(profile))
        {
            uint id = setting.SettingId;
            if (!available.Contains(id)) continue; // not offered by this driver
            if (!seen.Add(id)) continue;           // the driver repeats some IDs in mask sets
            if (curatedIds.Contains(id)) continue; // curated settings never repeat in Advanced

            var info = TrySettingInfo(setting);
            if (info is null) continue;
            bool isKnown;
            try { isKnown = info.IsKnown; }
            catch { isKnown = false; }

            if (!TryToUInt(setting.CurrentValue, out uint value)) continue;
            if (!TryToUInt(info.DefaultValue, out uint defaultValue)) continue;

            bool editable = TrySettingType(info) == DRSSettingType.Integer;
            var options = ReadLabelledOptions(id, info);

            if (isKnown)
            {
                var identity = NvidiaAdvancedClassifier.Classify(id, true, info.Name);
                known.Add(new Nvidia3DRow(
                    id, identity.Name, value, defaultValue, false, options,
                    identity.Provenance, editable));
            }
            else
            {
                // No verified mapping: hex ID only. Never invent a name or a meaning.
                var identity = NvidiaAdvancedClassifier.Classify(id, false, null);
                unknown.Add(new Nvidia3DRow(
                    id, identity.Name, value, defaultValue, false, options,
                    identity.Provenance, editable));
            }
        }
        return (known, unknown);
    }

    private static IEnumerable<ProfileSetting> EnumerateProfileSettings(DriverSettingsProfile profile)
    {
        try { return profile.Settings.Where(s => s is not null).ToList(); }
        catch { return Array.Empty<ProfileSetting>(); }
    }

    private static SettingInfo? TrySettingInfo(ProfileSetting setting)
    {
        try { return setting.SettingInfo ?? SettingInfo.FromId(setting.SettingId); }
        catch { return null; }
    }

    private static DRSSettingType? TrySettingType(SettingInfo info)
    {
        try { return info.SettingType; }
        catch { return null; }
    }

    /// <summary>
    /// The driver-reported option values, labelled from the SDK's own value metadata.
    /// Returns empty when the SDK has no name for any of the values, which is the
    /// signal to render a read-only row instead of an empty dropdown.
    /// </summary>
    private static IReadOnlyList<Nvidia3DOption> ReadLabelledOptions(uint id, SettingInfo info)
    {
        try
        {
            var values = info.AvailableValues ?? Array.Empty<object>();
            var pairs = new List<(uint, string?)>();
            foreach (var value in values)
            {
                if (!TryToUInt(value, out uint numeric)) continue;
                string? label = null;
                try { label = info.ResolveKnownValueName(value); } catch { label = null; }
                pairs.Add((numeric, label));
            }
            return Nvidia3DSettingsCatalog.BuildLabelledOptions(pairs);
        }
        catch { return Array.Empty<Nvidia3DOption>(); }
    }

    private static Nvidia3DRow? TryReadRow(
        DriverSettingsSession session, DriverSettingsProfile profile, DriverSettingsProfile? global,
        Nvidia3DProfileScope scope, Nvidia3DSettingDefinition definition)
    {
        try
        {
            var settingInfo = SettingInfo.FromId(definition.Id);
            var setting = profile.GetSetting(definition.Id);
            bool inherited = scope == Nvidia3DProfileScope.Program && setting is null;
            var effective = setting?.CurrentValue;
            var inheritedValue = global?.GetSetting(definition.Id)?.CurrentValue ?? settingInfo.DefaultValue;

            var options = ReadLabelledOptions(definition.Id, settingInfo);
            bool editable = TrySettingType(settingInfo) == DRSSettingType.Integer;

            // A non-integer DRS value (for example the literal string "none" on CUDA
            // excluded GPUs) cannot be written by this editor. Show the row read-only
            // with the value the driver reports rather than dropping it silently or
            // coercing it into a number.
            if (!TryToUInt(effective ?? inheritedValue, out uint value) || !TryToUInt(settingInfo.DefaultValue, out uint defaultValue))
            {
                string rawLabel = DescribeRawValue(effective ?? inheritedValue);
                return new Nvidia3DRow(
                    definition.Id, definition.Name, 0, 0, inherited, options,
                    NvidiaSettingProvenance.Curated,
                    IsEditable: false, ValueLabel: rawLabel);
            }

            if (scope == Nvidia3DProfileScope.Program)
                options = Nvidia3DSettingsCatalog.BuildOptions(options, true, null);

            return new Nvidia3DRow(
                definition.Id, definition.Name, value, defaultValue, inherited, options,
                NvidiaSettingProvenance.Curated, editable);
        }
        catch { return null; }
    }

    private static string DescribeRawValue(object? value)
        => value is null ? "(not set)" : value.ToString() ?? "(not set)";

    private static bool TryToUInt(object? value, out uint result)
    {
        result = 0;
        if (value is null) return true;
        if (value is uint number) { result = number; return true; }
        if (value is int signed && signed >= 0) { result = (uint)signed; return true; }
        if (value is byte[] bytes && bytes.Length >= 4) { result = BitConverter.ToUInt32(bytes, 0); return true; }
        if (value is string text) return uint.TryParse(text, out result);
        try { result = Convert.ToUInt32(value); return true; }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (OverflowException) { return false; }
    }

    public void Stage(uint id, uint value) => _staged[id] = value;
    public void ResetStage() => _staged.Clear();
    public IReadOnlyDictionary<uint, uint> StagedSnapshot() => new Dictionary<uint, uint>(_staged);

    public Nvidia3DApplyResult ApplyGlobal(IEnumerable<Nvidia3DRow> rows, bool restore)
        => ApplyCore(restore, isProgram: false, executable: null, rows);

    public Nvidia3DApplyResult ApplyProgram(string executable, IEnumerable<Nvidia3DRow> rows, bool restore = false)
        => ApplyCore(restore, isProgram: true, executable, rows);

    /// <summary>
    /// The one apply path, shared by the Simple view, the Advanced view and presets:
    /// stage -> backup -> write -> save -> read back -> report every setting.
    /// </summary>
    private Nvidia3DApplyResult ApplyCore(bool restore, bool isProgram, string? executable, IEnumerable<Nvidia3DRow> rows)
    {
        lock (NvApiSession.Gate)
        {
            try
            {
                NVIDIA.Initialize();
                var ids = rows.Select(r => r.Id).ToHashSet();
                var staged = StagedSnapshot();

                using (var session = DriverSettingsSession.CreateAndLoad())
                {
                    SaveBackup(session);
                    var profile = session.BaseProfile;
                    if (isProgram)
                    {
                        var app = session.FindApplication(executable ?? string.Empty);
                        profile = app?.Profile;
                        if (profile is null)
                        {
                            profile = DriverSettingsProfile.CreateProfile(session, Path.GetFileNameWithoutExtension(executable));
                            ProfileApplication.CreateApplication(profile, Path.GetFileName(executable), Path.GetFileNameWithoutExtension(executable));
                        }
                    }
                    if (restore)
                    {
                        DRSApi.RestoreDefaults(session.Handle, profile.Handle);
                    }
                    else
                    {
                        foreach (var row in rows)
                        {
                            if (!staged.TryGetValue(row.Id, out uint value)) continue;
                            if (isProgram && value == uint.MaxValue) profile.DeleteSetting(row.Id);
                            else profile.SetSetting(row.Id, DRSSettingType.Integer, value);
                        }
                    }
                    session.Save();
                }
                _staged.Clear();

                var outcomes = restore
                    ? Array.Empty<Nvidia3DSettingOutcome>()
                    : BuildOutcomes(ids, staged, isProgram, executable);
                int failed = outcomes.Count(o => !o.Applied);
                string scope = isProgram ? "Program profile" : "Global profile";
                string message = failed == 0
                    ? $"{scope} saved; {outcomes.Count} setting(s) verified."
                    : $"{scope} saved; {outcomes.Count - failed} of {outcomes.Count} setting(s) verified, {failed} did not take effect.";
                return new Nvidia3DApplyResult(true, message, BackupPath, outcomes);
            }
            catch (Exception ex) { return new Nvidia3DApplyResult(false, ex.Message); }
        }
    }

    /// <summary>
    /// Reads the just-written settings back and compares each one against what was
    /// staged, so a driver that silently refused a value is reported by name.
    /// </summary>
    private IReadOnlyList<Nvidia3DSettingOutcome> BuildOutcomes(
        IReadOnlySet<uint> ids, IReadOnlyDictionary<uint, uint> staged, bool isProgram, string? executable)
    {
        if (ids.Count == 0) return Array.Empty<Nvidia3DSettingOutcome>();
        var readBack = ReadBack(ids, isProgram, executable);
        var results = new List<Nvidia3DSettingOutcome>();
        foreach (var id in ids)
        {
            if (!staged.TryGetValue(id, out uint expected)) continue;
            string name = readBack.NameFor(id) ?? $"0x{id:X8}";
            bool inheritExpected = isProgram && expected == uint.MaxValue;
            if (!readBack.TryGet(id, out uint actual))
            {
                results.Add(new Nvidia3DSettingOutcome(id, name,
                    inheritExpected,
                    inheritExpected ? "setting removed; profile now inherits" : "driver did not report this setting after save",
                    expected, null));
                continue;
            }
            bool applied = inheritExpected ? false : actual == expected;
            results.Add(new Nvidia3DSettingOutcome(id, name, applied,
                applied ? "ok" : $"driver reports {actual} after save, expected {expected}",
                expected, actual));
        }
        return results;
    }

    private sealed class ReadBackIndex
    {
        public Dictionary<uint, uint> Values { get; } = new();
        public Dictionary<uint, string> Names { get; } = new();
        public bool TryGet(uint id, out uint value) => Values.TryGetValue(id, out value);
        public string? NameFor(uint id) => Names.TryGetValue(id, out var name) ? name : null;
    }

    private ReadBackIndex ReadBack(IReadOnlySet<uint> ids, bool isProgram, string? executable)
    {
        var index = new ReadBackIndex();
        lock (NvApiSession.Gate)
        {
            try
            {
                NVIDIA.Initialize();
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = session.BaseProfile;
                if (isProgram)
                {
                    var app = session.FindApplication(executable ?? string.Empty);
                    profile = app?.Profile ?? session.FindApplicationProfile(executable ?? string.Empty);
                }
                if (profile is null) return index;

                var curated = Nvidia3DSettingsCatalog.Expected.ToDictionary(d => d.Id, d => d);
                foreach (var id in ids)
                {
                    var setting = profile.GetSetting(id);
                    var info = setting?.SettingInfo;
                    if (info is not null && !string.IsNullOrWhiteSpace(info.Name)) index.Names[id] = info.Name;
                    else if (curated.TryGetValue(id, out var def)) index.Names[id] = def.Name;
                    if (setting is null) continue;
                    if (TryToUInt(setting.CurrentValue, out uint value)) index.Values[id] = value;
                }
            }
            catch { /* leave the index empty; callers report "did not report this setting" */ }
        }
        return index;
    }

    private static void SaveBackup(DriverSettingsSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        DRSApi.SaveSettings(session.Handle, BackupPath);
    }
}
