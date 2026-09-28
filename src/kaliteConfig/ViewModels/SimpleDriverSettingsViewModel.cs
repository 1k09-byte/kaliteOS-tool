// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of this software, in any form, whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

/// <summary>A profile the user can switch to, with enough detail to render an icon.</summary>
public sealed class NvidiaProfileOption
{
    public required string ProfileKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Subtitle { get; init; }
    public required bool IsGlobal { get; init; }
    public string? Executable { get; init; }

    /// <summary>Segoe glyph for the picker list, where a bitmap icon is not loaded yet.</summary>
    public string Glyph => IsGlobal ? "\uE774" : "\uE7F4";
}

/// <summary>A titled group of rows in the default view.</summary>
public sealed class NvidiaSettingSectionViewModel
{
    public required string Title { get; init; }
    public required ObservableCollection<NvidiaSettingRowViewModel> Rows { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>True for the "Hidden" section, which is styled down and collapsed by default.</summary>
    public bool IsMuted { get; init; }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}

/// <summary>
/// Drives the Simple Driver Settings tab.
///
/// The rows are the settings this driver actually offers, read from the driver rather
/// than from a list written by hand, so a setting this driver version does not have
/// never appears and one it adds appears without a code change. Names, value labels
/// and defaults are the driver's own; nothing is described in prose, because the
/// driver publishes no prose to describe it with.
///
/// The 3D settings page owns a handful of classic settings and the Simple page skips
/// those, so no setting is listed twice. Grouping is the user's own editable map, and
/// an unlisted ID lands in "Other" rather than being guessed at.
/// </summary>
public sealed partial class SimpleDriverSettingsViewModel : ObservableObject
{
    private readonly Nvidia3DSettingsService _service = new();
    private readonly NvidiaSimpleSettingsConfig _config = new();
    private readonly NvidiaSimpleSettingsStateStore _store = new();
    private NvidiaSettingCategoryMap _categories = NvidiaSettingCategoryMap.Empty;
    private NvidiaSimpleSettingsState _state = new();

    private readonly Dictionary<uint, Nvidia3DRow> _coreById = new();
    private readonly Dictionary<uint, NvidiaSettingRowViewModel> _rowVms = new();

    /// <summary>
    /// The profile the rows on screen belong to. Tracked explicitly rather than
    /// re-derived from display text, because Apply has to address the exact same
    /// profile the user was looking at.
    /// </summary>
    public NvidiaProfileOption? CurrentProfile { get; private set; }

    [ObservableProperty] private ObservableCollection<NvidiaSettingSectionViewModel> sections = new();
    [ObservableProperty] private ObservableCollection<NvidiaProfileOption> profiles = new();
    [ObservableProperty] private ObservableCollection<NvidiaPresetOption> presets = new();
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string applyReport = "";
    [ObservableProperty] private bool hasStagedChanges;
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string profileSubtitle = "";
    [ObservableProperty] private bool isGlobalProfile;
    [ObservableProperty] private bool requiresGlobalConfirmation;

    /// <summary>Plain-English diff of everything staged, shown before any Apply.</summary>
    [ObservableProperty] private ObservableCollection<string> diffLines = new();

    /// <summary>Rows the search matched, across every section, for the empty-state message.</summary>
    [ObservableProperty] private int visibleRowCount;

    /// <summary>
    /// Recently changed settings, across every profile, newest first. The profile each
    /// one came from is part of the line, because "Vertical sync is off" is a much less
    /// useful fact once you are editing a different game.
    /// </summary>
    [ObservableProperty] private ObservableCollection<NvidiaRecentChangeViewModel> recents = new();

    /// <summary>True when a search is active and nothing matched, so the page can say so.</summary>
    public bool HasNoMatches => !string.IsNullOrWhiteSpace(SearchText) && VisibleRowCount == 0;

    /// <summary>Only render the recents card when there is something to show.</summary>
    public bool HasRecents => Recents.Count > 0;

    /// <summary>Only show the status bar when there is something to say.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    /// <summary>Only show the apply report when there is one.</summary>
    public bool HasApplyReport => !string.IsNullOrWhiteSpace(ApplyReport);

    /// <summary>True when the driver offered no settings at all for this profile.</summary>
    public bool HasNoCuratedSettings => !IsBusy && Sections.Count == 0;

    private void RaiseTextFlags()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasApplyReport));
        OnPropertyChanged(nameof(HasNoCuratedSettings));
    }

    public string ProfileHeaderText => IsGlobalProfile
        ? $"{ProfileName} - affects every app without its own profile"
        : ProfileName;

    public bool HasStagedDiff => DiffLines.Count > 0;

    public SimpleDriverSettingsViewModel()
    {
        _categories = _config.Load();
        _state = _store.Load();
        foreach (var preset in NvidiaSettingPresets.All)
            Presets.Add(new NvidiaPresetOption(preset));
    }

    /// <summary>Re-reads the profile list and loads the currently selected profile.</summary>
    public async Task RefreshAsync(NvidiaProfileOption? select = null)
    {
        IsBusy = true;
        try
        {
            var apps = await Task.Run(() => _service.EnumerateApplications());
            Profiles = BuildProfileList(apps);
            var target = select is not null && Profiles.Any(p => p.ProfileKey == select.ProfileKey)
                ? select
                : Profiles.FirstOrDefault();
            CurrentProfile = Profiles.FirstOrDefault(p => p.ProfileKey == target?.ProfileKey);
            if (target is not null) await LoadProfileAsync(target);
            else SetProfileHeader("Base Profile", "No profile could be read", true);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; RaiseTextFlags(); }
    }

    private ObservableCollection<NvidiaProfileOption> BuildProfileList(IReadOnlyList<Nvidia3DApplication> apps)
    {
        var list = new ObservableCollection<NvidiaProfileOption>
        {
            new() { ProfileKey = "global", DisplayName = "Base Profile (Global)", Subtitle = "Applies to every app without its own profile", IsGlobal = true },
        };
        foreach (var app in apps)
        {
            string key = app.Executable;
            list.Add(new NvidiaProfileOption
            {
                ProfileKey = key,
                DisplayName = app.Label,
                Subtitle = System.IO.Path.GetFileName(app.Executable),
                IsGlobal = false,
                Executable = app.Executable,
            });
        }
        return list;
    }

    /// <summary>Switches to a profile. Staged edits from another profile are discarded.</summary>
    [RelayCommand]
    public async Task LoadProfileAsync(NvidiaProfileOption? profile)
    {
        if (profile is null) return;
        IsBusy = true;
        try
        {
            _service.ResetStage();
            HasStagedChanges = false;
            DiffLines.Clear();
            ApplyReport = string.Empty;
            CurrentProfile = profile;

            var snapshot = await Task.Run(() =>
                _service.ReadSimpleSnapshot(!profile.IsGlobal, profile.Executable));
            ApplyReport = string.Empty;
            Status = "";

            _coreById.Clear();
            foreach (var row in snapshot.Simple) _coreById[row.Id] = row;

            SetProfileHeader(snapshot.ProfileName,
                profile.IsGlobal ? "Applies to every app without its own profile" : profile.Subtitle,
                snapshot.IsGlobal);

            RebuildSimple(snapshot.Simple);
            // Never a silent blank page: say what happened instead.
            Status = snapshot.Simple.Count == 0
                ? "This driver offers no settings for this profile."
                : "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; RaiseTextFlags(); }
    }

    private void SetProfileHeader(string name, string subtitle, bool isGlobal)
    {
        ProfileName = name;
        ProfileSubtitle = subtitle;
        IsGlobalProfile = isGlobal;
        RequiresGlobalConfirmation = isGlobal;
        OnPropertyChanged(nameof(ProfileHeaderText));
    }

    // ---------------------------------------------------------------- default view

    private void RebuildSimple(IReadOnlyList<Nvidia3DRow> rows)
    {
        // One view model per setting, reused across every relayout. Rebuilding them
        // on each star would throw away the control state the user has staged.
        _rowVms.Clear();
        foreach (var projected in rows.Select(Project)) _rowVms[projected.Id] = MakeRowViewModel(projected);
        RelayoutSections();
        RelayoutRecents();
    }

    /// <summary>
    /// Re-buckets the row view models into "My Settings" plus the outcome
    /// categories. Called on load and again whenever a star changes, so pinning a
    /// setting moves it immediately without losing anything staged.
    /// </summary>
    private void RelayoutSections()
    {
        var sectionList = new List<NvidiaSettingSectionViewModel>();

        // A starred setting appears once, in "My Settings", and leaves its category.
        var favorites = _rowVms.Values.Where(r => r.IsFavorite)
            .OrderBy(r => r.CategoryTitle, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        if (favorites.Count > 0)
        {
            sectionList.Add(new NvidiaSettingSectionViewModel
            {
                Title = "My Settings",
                Subtitle = "Settings you starred",
                Rows = new ObservableCollection<NvidiaSettingRowViewModel>(favorites),
            });
        }

        foreach (var category in NvidiaSettingCategories.Ordered)
        {
            var inCategory = _rowVms.Values
                .Where(r => r.Row.Category == category && !r.IsFavorite)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .ToList();
            if (inCategory.Count == 0) continue;
            sectionList.Add(new NvidiaSettingSectionViewModel
            {
                Title = category.DisplayName(),
                Subtitle = category == NvidiaSettingCategory.Hidden
                    ? "Settings most single-GPU machines never need. Collapsed so they stay out of the way."
                    : null,
                IsMuted = category.IsMuted(),
                Rows = new ObservableCollection<NvidiaSettingRowViewModel>(inCategory),
            });
        }

        Sections = new ObservableCollection<NvidiaSettingSectionViewModel>(sectionList);
        OnPropertyChanged(nameof(HasNoCuratedSettings));
        ApplySearch();
    }

    private void RelayoutRecents()
    {
        Recents = new ObservableCollection<NvidiaRecentChangeViewModel>(BuildRecents());
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <summary>
    /// Turns the persisted recents into one readable line each. Entries belonging to the
    /// profile on screen are marked, so the list stays useful while switching games.
    /// </summary>
    private List<NvidiaRecentChangeViewModel> BuildRecents()
    {
        string currentKey = CurrentProfile?.ProfileKey ?? "global";
        return _state.RecentChanges
            .OrderByDescending(r => r.TimestampUtc)
            .Take(NvidiaSimpleSettingsState.MaxRecents)
            .Select(r => new NvidiaRecentChangeViewModel(r,
                string.Equals(r.ProfileKey, currentKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private NvidiaSimpleSettingRow Project(Nvidia3DRow row) => new(
        row.Id, row.Name, row.Value, row.DefaultValue, row.IsInherited,
        row.Options, _categories.CategoryFor(row.Id), row.Provenance, row.IsEditable, row.HexId);

    // ------------------------------------------------------------------ advanced

    // -------------------------------------------------------------------- search

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void ApplySearch()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var section in Sections)
                foreach (var row in section.Rows) row.IsVisible = true;
        }
        else
        {
            foreach (var section in Sections)
                foreach (var row in section.Rows)
                    row.IsVisible = NvidiaSimpleSettings.MatchesSearch(SearchText, row.Name, row.HexId);
        }
        VisibleRowCount = Sections.Sum(s => s.Rows.Count(r => r.IsVisible));
        OnPropertyChanged(nameof(HasNoMatches));
        ApplyStagedDiff();
    }

    // ------------------------------------------------------------------- staging

    private NvidiaSettingRowViewModel MakeRowViewModel(NvidiaSimpleSettingRow row)
    {
        var vm = new NvidiaSettingRowViewModel(row, Stage, _state.IsFavorite(row.Id));
        vm.FavoriteChanged += (id, isFavorite) =>
        {
            if (isFavorite) _state.FavoriteIds.Add(id);
            else _state.FavoriteIds.Remove(id);
            _store.Save(_state);
            // Move the row between "My Settings" and its category straight away.
            RelayoutSections();
        };
        return vm;
    }

    private void Stage(NvidiaSimpleSettingRow row, uint value)
    {
        if (!_coreById.TryGetValue(row.Id, out var core)) return;
        _service.Stage(row.Id, value);
        HasStagedChanges = true;

        _state.RecordChange(new NvidiaRecentSettingChange(
            row.Id, CurrentProfile?.ProfileKey ?? "global", row.Name, row.CurrentLabel,
            NvidiaSimpleSettings.FormatValue(value, row.Options, row.IsInherited),
            DateTimeOffset.UtcNow));
        _store.Save(_state);
        RelayoutRecents();
        ApplyStagedDiff();
    }

    /// <summary>Rebuilds the plain-English diff from the service's staged set.</summary>
    private void ApplyStagedDiff()
    {
        var staged = _service.StagedSnapshot();
        var lines = new List<string>();
        foreach (var (id, value) in staged)
        {
            if (!_coreById.TryGetValue(id, out var core)) continue;
            var projected = Project(core);
            lines.Add(NvidiaSimpleSettings.BuildDiffLine(
                core.Name,
                NvidiaSimpleSettings.FormatValue(core.Value, core.Options, core.IsInherited),
                NvidiaSimpleSettings.FormatValue(value, core.Options, core.IsInherited)));
        }
        DiffLines = new ObservableCollection<string>(lines);
        OnPropertyChanged(nameof(HasStagedDiff));
    }

    /// <summary>Re-runs the text-visibility flags; called whenever a message changes.</summary>
    partial void OnStatusChanged(string value) => RaiseTextFlags();

    partial void OnApplyReportChanged(string value) => RaiseTextFlags();

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoCuratedSettings));

    // -------------------------------------------------------------------- presets

    /// <summary>Resolves a preset against the live rows for the confirmation dialog.</summary>
    public NvidiaPresetResolution PreviewPreset(NvidiaSettingPreset preset, IReadOnlySet<uint>? excluded = null)
        => NvidiaSettingPresets.Resolve(preset, _rowVms.Values.Select(r => r.Row).ToList(), excluded);

    /// <summary>Stages every applicable step of a preset and leaves the rest out.</summary>
    [RelayCommand]
    public void StagePreset(NvidiaSettingPreset preset) => StagePreset(preset, null);

    public void StagePreset(NvidiaSettingPreset preset, IReadOnlySet<uint>? excluded)
    {
        var resolution = PreviewPreset(preset, excluded);
        foreach (var step in resolution.Applicable)
        {
            if (step.Value is null || !_rowVms.TryGetValue(step.SettingId, out var row)) continue;
            row.SelectedOption = row.Options.FirstOrDefault(o => o.Value == step.Value.Value);
        }
        ApplyReport = resolution.Skipped.Count == 0
            ? $"'{preset.Name}' staged."
            : $"'{preset.Name}' staged with {resolution.Skipped.Count} setting(s) left out: "
                + string.Join("; ", resolution.Skipped.Select(s => $"{s.Name} ({s.Reason})"));
    }

    /// <summary>Throws away everything staged so far, in one step.</summary>
    [RelayCommand]
    public void DiscardStaged()
    {
        _service.ResetStage();
        HasStagedChanges = false;
        DiffLines.Clear();
        OnPropertyChanged(nameof(HasStagedDiff));
        ApplyReport = "Staged changes discarded.";
    }

    // ---------------------------------------------------------------------- apply

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!HasStagedChanges) return;
        IsBusy = true;
        try
        {
            var result = IsGlobalProfile
                ? _service.ApplyGlobal(_coreById.Values.ToList(), restore: false)
                : _service.ApplyProgram(CurrentProfile?.Executable ?? "", _coreById.Values.ToList(), restore: false);
            ApplyReport = DescribeApply(result);
            Status = result.Success ? "" : result.Message;
            if (result.Success) await ReloadCurrentProfileAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; RaiseTextFlags(); }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        try
        {
            var result = IsGlobalProfile
                ? _service.ApplyGlobal(_coreById.Values.ToList(), restore: true)
                : _service.ApplyProgram(CurrentProfile?.Executable ?? "", _coreById.Values.ToList(), restore: true);
            ApplyReport = DescribeApply(result);
            if (result.Success) await ReloadCurrentProfileAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Re-reads the profile just written, so the rows show what the driver took.</summary>
    private async Task ReloadCurrentProfileAsync()
    {
        var current = CurrentProfile;
        if (current is not null) await LoadProfileAsync(current);
    }

    /// <summary>Turns a per-setting apply result into one honest sentence per setting.</summary>
    public static string DescribeApply(Nvidia3DApplyResult result)
    {
        if (!result.Success) return $"Apply failed: {result.Message}";
        if (result.OutcomeList.Count == 0) return result.Message;
        var lines = new List<string> { result.Message };
        foreach (var outcome in result.OutcomeList)
            lines.Add($"{(outcome.Applied ? "Applied" : "Not applied")} - {outcome.Name}: {outcome.Reason}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>One entry in the "Recently changed" list.</summary>
public sealed class NvidiaRecentChangeViewModel
{
    public NvidiaRecentChangeViewModel(NvidiaRecentSettingChange change, bool isCurrentProfile)
    {
        Text = $"{change.Name}: {change.FromLabel} to {change.ToLabel}";
        ProfileName = change.ProfileKey == "global" ? "Base Profile" : change.ProfileKey;
        When = DescribeWhen(change.TimestampUtc);
        IsCurrentProfile = isCurrentProfile;
    }

    public string Text { get; }
    public string ProfileName { get; }
    public string When { get; }
    public bool IsCurrentProfile { get; }

    /// <summary>Plain English age, because a raw timestamp is not a useful summary.</summary>
    private static string DescribeWhen(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d");
    }
}

/// <summary>A preset as offered in the UI.</summary>
public sealed class NvidiaPresetOption
{
    public NvidiaSettingPreset Preset { get; }
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public NvidiaPresetOption(NvidiaSettingPreset preset) => Preset = preset;
}

/// <summary>
/// One setting row. Owns the friendly control and writes any change straight into the
/// shared stage, so what is staged always matches what is on screen.
/// </summary>
public sealed partial class NvidiaSettingRowViewModel : ObservableObject
{
    private readonly Action<NvidiaSimpleSettingRow, uint> _stage;
    private uint _value;
    private bool _suppressStage;

    public event Action<uint, bool>? FavoriteChanged;

    public NvidiaSimpleSettingRow Row { get; }
    public uint Id => Row.Id;
    public string HexId => Row.HexId;
    public string Name => Row.Name;
    public string CategoryTitle => Row.Category.DisplayName();
    public bool IsEditable => Row.IsEditable;
    public bool IsInherited => Row.IsInherited;
    public NvidiaSettingProvenance Provenance => Row.Provenance;
    public bool IsUnverified => Row.Provenance == NvidiaSettingProvenance.Unknown;
    public bool IsAdvancedOnly => Row.Provenance != NvidiaSettingProvenance.Curated;
    public ObservableCollection<Nvidia3DOption> Options { get; } = new();
    public NvidiaSettingControlKind ControlKind => Row.ControlKind;

    public bool IsToggleControl => ControlKind == NvidiaSettingControlKind.Toggle;
    public bool IsChoiceControl => ControlKind == NvidiaSettingControlKind.Choice;
    public bool IsReadOnlyControl => ControlKind == NvidiaSettingControlKind.ReadOnly;

    /// <summary>Free-text box for raw Advanced values; committed with Enter.</summary>
    [ObservableProperty] private string rawInput = "";

    [ObservableProperty] private bool isVisible = true;
    [ObservableProperty] private bool isFavorite;

    /// <summary>Read-only display text: the resolved label, the raw value, or the inherit marker.</summary>
    public string ValueText => !IsEditable && Row.ValueLabel is not null
        ? Row.ValueLabel
        : NvidiaSimpleSettings.FormatValue(Value, Row.Options, Row.IsInherited);

    /// <summary>Raw value for the Advanced editor, in both hex and decimal.</summary>
    public string RawValueText => $"0x{Value:X8} ({Value})";

    public string UnverifiedNote => IsUnverified
        ? "The driver does not publish a name for this setting, so it is shown by its ID. Edit only if you know what it does."
        : string.Empty;

    public uint Value
    {
        get => _value;
        private set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(SelectedOption));
            if (!_suppressStage) _stage(Row, value);
        }
    }

    public Nvidia3DOption? SelectedOption
    {
        get => Options.FirstOrDefault(o => o.Value == Value);
        set
        {
            if (value is null) return;
            Value = value.Value;
        }
    }

    /// <summary>Toggle state, derived from whichever option label reads as "off".</summary>
    public bool IsOn
    {
        get
        {
            var selected = SelectedOption;
            if (selected is not null) return !NvidiaSimpleSettings.IsOffLabel(selected.Label);
            return Value != 0 && !Row.IsInherited;
        }
        set
        {
            if (!IsEditable) return;
            // "Mask" is the driver's placeholder for "no specific choice", never an
            // enabled state - so it can never be what a switch turns on.
            var off = Options.FirstOrDefault(o => NvidiaSimpleSettings.IsOffLabel(o.Label)
                && o.Value != NvidiaSimpleSettingRow.InheritSentinel);
            var on = Options.FirstOrDefault(o => !NvidiaSimpleSettings.IsOffLabel(o.Label)
                && !NvidiaSimpleSettings.IsMaskLabel(o.Label)
                && o.Value != NvidiaSimpleSettingRow.InheritSentinel);
            if (value ? on is null : off is null) return;
            Value = (value ? on : off)!.Value;
        }
    }

    public NvidiaSettingRowViewModel(NvidiaSimpleSettingRow row, Action<NvidiaSimpleSettingRow, uint> stage, bool isFavorite)
    {
        Row = row;
        _stage = stage;
        IsFavorite = isFavorite;
        foreach (var option in row.Options) Options.Add(option);
        _value = row.IsInherited ? NvidiaSimpleSettingRow.InheritSentinel : row.CurrentValue;
        RawInput = $"0x{_value:X}";
    }
    /// <summary>
    /// Stages a raw value typed into the Advanced editor. Accepts hex or decimal and
    /// refuses anything else, so a mistyped value never reaches the driver.
    /// </summary>
    public bool TryStageRawValue(string? text)
    {
        if (!IsEditable) return false;
        if (!NvidiaSettingCategoryMap.TryParseIdText(text, out uint parsed)) return false;
        _suppressStage = false;
        Value = parsed;
        return true;
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotFavorite));
        FavoriteChanged?.Invoke(Id, value);
    }

    /// <summary>Inverse of <see cref="IsFavorite"/>, for the outline/filled star pair.</summary>
    public bool IsNotFavorite => !IsFavorite;
}
