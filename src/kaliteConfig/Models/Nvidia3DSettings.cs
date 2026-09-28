using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Models;

public enum Nvidia3DProfileScope { Global, Program }

/// <summary>
/// One curated, NVCP-visible DRS setting. This list is only which settings the 3D
/// page owns; the Simple page reads the driver's own enumeration instead.
/// </summary>
public sealed record Nvidia3DSettingDefinition(
    uint Id, string Name, string Source, bool Global, bool Program);

public sealed record Nvidia3DDriverSetting(uint Id, string Name);
public sealed class Nvidia3DOption
{
    public uint Value { get; set; }
    public string Label { get; set; }
    public Nvidia3DOption(uint value, string label) { Value = value; Label = label; }
}

/// <summary>Curated NVCP-visible settings resolved from the LLT NvAPIWrapper KnownSettingId mapping.</summary>
public static class Nvidia3DSettingsCatalog
{
    public static readonly IReadOnlyList<Nvidia3DSettingDefinition> Expected = new Nvidia3DSettingDefinition[]
    {
        new(0x101E61A9, "Anisotropic filtering", "LLT KnownSettingId.AnisotropicModeLevel", true, true),
        new(0x10D2BB16, "Anisotropic filtering mode", "LLT KnownSettingId.AnisotropicModeSelector", true, true),
        new(0x107EFC5B, "Antialiasing - Mode", "LLT KnownSettingId.AntiAliasingModeSelector", true, true),
        new(0x107D639D, "Antialiasing - Gamma correction", "LLT KnownSettingId.AntiAliasingModeGammaCorrection", true, true),
        new(0x10D773D2, "Antialiasing - Setting", "LLT KnownSettingId.AntiAliasingModeMethod", true, true),
        new(0x1034CB89, "Antialiasing - FXAA permission", "LLT KnownSettingId.FXAAAllow", true, true),
        new(0x1074C972, "Antialiasing - FXAA", "LLT KnownSettingId.FXAAEnable", true, true),
        new(0x10FC2D9C, "Antialiasing - Transparency multisampling", "LLT KnownSettingId.AntiAliasingModeAlphaToCoverage", true, true),
        new(0x10D48A85, "Antialiasing - Transparency supersampling", "LLT KnownSettingId.AntiAliasingModeReplay", true, true),
        new(0x10835006, "Background application max frame rate", "Profile Inspector Reference.xml: Background Application Max Frame Rate For Nvcpl", true, false),
        new(0x10354FF8, "CUDA - GPUs", "LLT KnownSettingId.CUDAExcludedGPUs", true, true),
        new(0x10835002, "Max Frame Rate", "LLT KnownSettingId.FrlFPS", true, true),
        new(0x0098C1AC, "MFAA", "LLT KnownSettingId.MaxwellBSampleInterleave", true, true),
        new(0x20D0F3E6, "OpenGL rendering GPU", "LLT KnownSettingId.OpenGLImplicitGPUAffinity", true, true),
        new(0x1057EB71, "Power management mode", "LLT KnownSettingId.PreferredPerformanceState", true, true),
        new(0x0064B541, "Preferred refresh rate", "LLT KnownSettingId.RefreshRateOverride", true, true),
        new(0x00198FFF, "Shader Cache", "LLT KnownSettingId.PerformanceStateShaderDiskCache", true, true),
        new(0x00E73211, "Texture filtering - Anisotropic sample optimization", "LLT KnownSettingId.PerformanceStateTextureFilteringAnisotropicOptimization", true, true),
        new(0x0084CD70, "Texture filtering - Anisotropic filter optimization", "LLT KnownSettingId.PerformanceStateTextureFilteringBiLinearInAnisotropic", true, true),
        new(0x0019BB68, "Texture filtering - Negative LOD bias", "LLT KnownSettingId.PerformanceStateTextureFilteringNoNegativeLODBias", true, true),
        new(0x00CE2691, "Texture filtering - Quality", "LLT KnownSettingId.QualityEnhancements", true, true),
        new(0x002ECAF2, "Texture filtering - Trilinear optimization", "LLT KnownSettingId.PerformanceStateTextureFilteringDisableTrilinearSlope", true, true),
        new(0x20C1221E, "Threaded optimization", "LLT KnownSettingId.OpenGLThreadControl", true, true),
        new(0x20FDD1F9, "Triple buffering", "LLT KnownSettingId.OpenGLTripleBuffer", true, true),
        new(0x00A879CF, "Vertical sync", "LLT KnownSettingId.VSyncMode", true, true),
        new(0x005A375C, "Vertical sync tear control", "LLT KnownSettingId.VSyncTearControl", true, true),
        new(0x10111133, "Virtual Reality pre-rendered frames", "LLT KnownSettingId.VRPreRenderLimit", true, true),
    };

    public static IReadOnlyList<Nvidia3DSettingDefinition> Resolve(IEnumerable<Nvidia3DDriverSetting> driverSettings, Nvidia3DProfileScope scope)
    {
        var byId = driverSettings.ToDictionary(s => s.Id);
        return Expected.Where(e => (scope == Nvidia3DProfileScope.Global ? e.Global : e.Program) && byId.ContainsKey(e.Id))
            .Select(e => e with { Name = byId[e.Id].Name }).ToArray();
    }

    public static IReadOnlyList<Nvidia3DSettingDefinition> Missing(IEnumerable<Nvidia3DDriverSetting> driverSettings)
    {
        var ids = new HashSet<uint>(driverSettings.Select(s => s.Id));
        return Expected.Where(e => !ids.Contains(e.Id)).ToArray();
    }

    public static IReadOnlyList<Nvidia3DDriverSetting> ExtraVisibleCandidates(IEnumerable<Nvidia3DDriverSetting> driverSettings)
    {
        var expected = new HashSet<uint>(Expected.Select(e => e.Id));
        return driverSettings.Where(s => !expected.Contains(s.Id)).ToArray();
    }

    /// <summary>
    /// Builds the option list for a setting from what the driver actually reports.
    /// <paramref name="driverValues"/> is the value/label pair list the driver gave us
    /// (label may be null when the SDK has no name for that value).
    ///
    /// Why this replaces any hand-written option table: the real value space for these
    /// settings is not "0, 1, 2". Vertical sync, for example, reports hashed values such
    /// as 1620202130 for passive, and texture filtering quality reports 4294967286 for
    /// high quality. A hardcoded table silently offers values the driver never accepts,
    /// or labels the wrong value. These labels come from the SDK's own SettingValues
    /// metadata, so they are correct by construction.
    ///
    /// Values the SDK cannot name are dropped rather than shown as bare numbers, and the
    /// first label wins for duplicate values (the driver repeats entries in mask sets).
    /// </summary>
    public static IReadOnlyList<Nvidia3DOption> BuildLabelledOptions(IEnumerable<(uint Value, string? Label)> driverValues)
    {
        var result = new List<Nvidia3DOption>();
        var seen = new HashSet<uint>();
        var values = driverValues.ToList();
        foreach (var (value, label) in values)
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            if (uint.TryParse(label, out _)) continue;   // a bare number is not a label
            if (!seen.Add(value)) continue;             // driver repeats values in mask sets
            result.Add(new Nvidia3DOption(value, NvidiaSimpleSettings.PrettifyLabel(label)));
        }
        if (result.Count == 0)
        {
            // The DLSS and Streamline overrides report no value names at all, which
            // would leave them as rows nobody can actually change. When a setting is
            // exactly the pair {0, 1} and names nothing, it is a boolean: every
            // boolean the driver *does* name uses 0 for off and 1 for on without
            // exception, so this reads the driver's own convention rather than
            // inventing one. Any other value space stays unlabelled, because there
            // the meaning really is unknown.
            var distinct = values.Select(v => v.Value).Distinct().OrderBy(v => v).ToList();
            if (distinct.Count == 2 && distinct[0] == 0 && distinct[1] == 1)
            {
                result.Add(new Nvidia3DOption(0, "Off"));
                result.Add(new Nvidia3DOption(1, "On"));
            }
        }
        return result;
    }

    public static IReadOnlyList<Nvidia3DOption> BuildOptions(IEnumerable<Nvidia3DOption> driverOptions, bool isProgram, uint? globalValue)
    {
        var result = driverOptions.GroupBy(option => option.Value).Select(group => group.First()).ToList();
        if (isProgram && !result.Any(option => option.Value == uint.MaxValue))
            result.Insert(0, new Nvidia3DOption(uint.MaxValue, "Use global setting"));
        return result;
    }

    public static bool IsInheritedProgramValue(bool settingExists) => !settingExists;
}
