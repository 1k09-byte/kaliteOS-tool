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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kaliteConfig.Models;

/// <summary>
/// One intended change inside a preset: a curated setting, plus the option labels that
/// would satisfy it, in preference order.
///
/// Candidates exist because NVIDIA spells the same option differently across driver
/// generations - the same "off" is "Off" on one driver and "ForceOff" on another. The
/// resolver picks the first label the running driver actually offers, so a preset is
/// never silently downgraded just because the wording changed.
/// </summary>
public sealed record NvidiaPresetStep(uint SettingId, IReadOnlyList<string> TargetLabels)
{
    public string DescribeTargets() => string.Join(" or ", TargetLabels.Select(t => $"\"{t}\""));
}

/// <summary>
/// A named bundle of changes over the curated settings only. Steps name a target by
/// its human label, never by a raw number, and are resolved against the live driver's
/// options at apply time - a driver that does not offer the label simply drops that
/// step instead of writing a wrong value.
/// </summary>
public sealed record NvidiaSettingPreset(string Id, string Name, string Description, IReadOnlyList<NvidiaPresetStep> Steps);

/// <summary>Outcome of resolving one preset step against the rows actually on screen.</summary>
public sealed record NvidiaPresetStepResolution(
    uint SettingId,
    string Name,
    bool Resolved,
    uint? Value,
    string FromLabel,
    string? TargetLabel,
    string Reason)
{
    public string DiffText => Resolved
        ? NvidiaSimpleSettings.BuildDiffLine(Name, FromLabel, TargetLabel ?? "")
        : $"{Name}: skipped - {Reason}";
}

/// <summary>Result of resolving a whole preset against the current rows.</summary>
public sealed record NvidiaPresetResolution(
    string PresetName,
    IReadOnlyList<NvidiaPresetStepResolution> Steps)
{
    public IReadOnlyList<NvidiaPresetStepResolution> Applicable => Steps.Where(s => s.Resolved).ToList();

    public IReadOnlyList<NvidiaPresetStepResolution> Skipped => Steps.Where(s => !s.Resolved).ToList();

    /// <summary>Plain-English diff for every applicable step, which is what the user confirms.</summary>
    public IReadOnlyList<string> DiffLines
        => Applicable.Select(s => NvidiaSimpleSettings.BuildDiffLine(s.Name, s.FromLabel, s.TargetLabel ?? "")).ToList();
}

/// <summary>
/// The built-in presets. Every step targets a curated setting and names the option in
/// plain words; the actual value is looked up in the driver-reported options at apply
/// time, so nothing here hardcodes a number a driver might not accept.
/// </summary>
public static class NvidiaSettingPresets
{
    /// <summary>
    /// Preset targets are settings the Simple Driver Settings page shows. The NVIDIA 3D
    /// settings page owns the classic knobs (power management mode, threaded
    /// optimisation, max frame rate), so a preset reaching across into them would
    /// quietly change a setting the user cannot see move.
    /// </summary>
    public const uint MaximumPreRenderedFrames = 0x007BA09E;
    public const uint OpenGLSwapInterval = 0x206A6582;
    public const uint BufferFlippingMode = 0x201F619F;
    public const uint TextureFilteringLodBias = 0x00738E8F;
    public const uint TextureFilteringQualitySubstitution = 0x00CE2692;
    public const uint AmbientOcclusion = 0x00667329;
    public const uint AntialiasingLineGamma = 0x2089BF6C;
    public const uint PowerThrottle = 0x00AE785C;
    public const uint ExternalQuietMode = 0x10115C8D;

    private static NvidiaPresetStep Step(uint id, params string[] targets) => new(id, targets);

    /// <summary>
    /// Bundles of settings a user can apply in one action.
    ///
    /// Each description says which settings the preset changes and nothing about what
    /// that will do to the picture or the frame rate. The driver publishes no
    /// description for these settings, so any promised outcome would be this app
    /// guessing. A step the driver does not offer is skipped and reported by name, so
    /// what a preset actually changed is always visible before and after applying it.
    /// </summary>
    public static IReadOnlyList<NvidiaSettingPreset> All { get; } = new NvidiaSettingPreset[]
    {
        new("reduce-input-lag", "Reduce input lag",
            "Sets Maximum pre-rendered frames to Maximum, OpenGL default swap interval to Tear, and Buffer-flipping mode to On.",
            new[]
            {
                Step(MaximumPreRenderedFrames, "Maximum"),
                Step(OpenGLSwapInterval, "Tear"),
                Step(BufferFlippingMode, "On"),
            }),

        new("best-image-quality", "Best image quality",
            "Sets Texture filtering - LOD Bias to Maximum, Texture filtering - Quality Substitution to No substitution, Ambient Occlusion to High, and Antialiasing - Line gamma to Enabled.",
            new[]
            {
                Step(TextureFilteringLodBias, "Maximum"),
                Step(TextureFilteringQualitySubstitution, "NoSubstitution"),
                Step(AmbientOcclusion, "High"),
                Step(AntialiasingLineGamma, "Enabled"),
            }),

        new("quiet-efficient", "Quiet / efficient",
            "Sets PowerThrottle to On and External Quiet Mode to On.",
            new[]
            {
                Step(PowerThrottle, "On"),
                Step(ExternalQuietMode, "On"),
            }),
    };

    public static NvidiaSettingPreset? ById(string? id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns a preset into concrete staged values against the rows the driver actually
    /// reported. A step is dropped - never guessed - when the setting is missing from
    /// this profile or the driver offers none of the wanted labels. Dropped steps come
    /// back with the reason, so the UI can name exactly which setting was left out and
    /// why while the rest of the preset still applies.
    /// </summary>
    public static NvidiaPresetResolution Resolve(
        NvidiaSettingPreset preset, IReadOnlyList<NvidiaSimpleSettingRow> rows, IReadOnlySet<uint>? excluded = null)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var steps = new List<NvidiaPresetStepResolution>();
        foreach (var step in preset.Steps)
        {
            if (!byId.TryGetValue(step.SettingId, out var row))
            {
                steps.Add(new NvidiaPresetStepResolution(step.SettingId,
                    $"Setting 0x{step.SettingId:X8}", false, null, string.Empty, null,
                    "not one of this profile's settings"));
                continue;
            }
            if (excluded is not null && excluded.Contains(step.SettingId))
            {
                steps.Add(new NvidiaPresetStepResolution(step.SettingId, row.Name, false, null,
                    row.CurrentLabel, null, "excluded from this preset"));
                continue;
            }
            if (!row.IsEditable)
            {
                steps.Add(new NvidiaPresetStepResolution(step.SettingId, row.Name, false, null,
                    row.CurrentLabel, null, "this setting cannot be edited here"));
                continue;
            }
            var option = row.Options.FirstOrDefault(o =>
                o.Value != NvidiaSimpleSettingRow.InheritSentinel
                && step.TargetLabels.Any(t => NvidiaSimpleSettings.LabelsMatch(o.Label, t)));
            if (option is null)
            {
                steps.Add(new NvidiaPresetStepResolution(step.SettingId, row.Name, false, null,
                    row.CurrentLabel, null, $"this driver does not offer {step.DescribeTargets()}"));
                continue;
            }
            steps.Add(new NvidiaPresetStepResolution(step.SettingId, row.Name, true, option.Value,
                row.CurrentLabel, option.Label, "ok"));
        }
        return new NvidiaPresetResolution(preset.Name, steps);
    }
}

/// <summary>A setting the user starred, pinned to the "My Settings" section.</summary>
public sealed record NvidiaSettingFavorite(uint SettingId);

/// <summary>One recently-changed entry, tagged with the profile it was changed on.</summary>
public sealed record NvidiaRecentSettingChange(
    uint SettingId,
    string ProfileKey,
    string Name,
    string FromLabel,
    string ToLabel,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Favorites and recents, persisted next to the app's other per-machine state. The
/// payload is a plain DTO so the persistence format can be tested without touching
/// the filesystem, and so a future field can be added without breaking old saves.
/// </summary>
public sealed class NvidiaSimpleSettingsState
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("favorites")]
    public List<uint> FavoriteIds { get; set; } = new();

    [JsonPropertyName("recentChanges")]
    public List<NvidiaRecentSettingChange> RecentChanges { get; set; } = new();

    /// <summary>Recents are capped so the file cannot grow without bound.</summary>
    public const int MaxRecents = 25;

    public bool IsFavorite(uint id) => FavoriteIds.Contains(id);

    public bool ToggleFavorite(uint id)
    {
        if (IsFavorite(id)) { FavoriteIds.Remove(id); return false; }
        FavoriteIds.Add(id);
        return true;
    }

    /// <summary>Newest first, de-duplicated so a setting changed twice shows once.</summary>
    public IReadOnlyList<NvidiaRecentSettingChange> RecentForProfile(string profileKey)
        => RecentChanges
            .Where(r => string.Equals(r.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.SettingId)
            .Select(g => g.OrderByDescending(r => r.TimestampUtc).First())
            .OrderByDescending(r => r.TimestampUtc)
            .ToList();

    public void RecordChange(NvidiaRecentSettingChange change)
    {
        RecentChanges.RemoveAll(r => r.SettingId == change.SettingId
            && string.Equals(r.ProfileKey, change.ProfileKey, StringComparison.OrdinalIgnoreCase));
        RecentChanges.Add(change);
        if (RecentChanges.Count > MaxRecents)
            RecentChanges = RecentChanges.OrderByDescending(r => r.TimestampUtc).Take(MaxRecents).ToList();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// Never throws: a corrupt or future file yields an empty state rather than an
    /// exception on startup. A newer schema is read but its unknown fields are ignored.
    /// </summary>
    public static NvidiaSimpleSettingsState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new NvidiaSimpleSettingsState();
        try
        {
            var parsed = JsonSerializer.Deserialize<NvidiaSimpleSettingsState>(json, JsonOpts);
            if (parsed is null) return new NvidiaSimpleSettingsState();
            parsed.FavoriteIds ??= new List<uint>();
            parsed.RecentChanges ??= new List<NvidiaRecentSettingChange>();
            return parsed;
        }
        catch (JsonException) { return new NvidiaSimpleSettingsState(); }
    }
}
