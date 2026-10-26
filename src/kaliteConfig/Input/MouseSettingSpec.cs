using System;
using System.Collections.Generic;

namespace kaliteConfig.Input;

public enum MouseSettingKind
{
    Toggle,
    Choice,
    Number,
}

public static class MouseSettingGroup
{
    public const string Sensor = "Sensor";
    public const string Filters = "Filters";
    public const string Buttons = "Buttons";
}

public sealed class MouseSettingSpec
{
    public MouseSettingSpec(string id, string label, string group, MouseSettingKind kind)
    {
        Id = id ?? string.Empty;
        Label = label ?? string.Empty;
        Group = group ?? string.Empty;
        Kind = kind;
    }

    public string Id { get; }
    public string Label { get; }
    public string Group { get; }
    public MouseSettingKind Kind { get; }
    public string Hint { get; init; } = string.Empty;
    public bool On { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public int Index { get; init; }
    public int Number { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public double Step { get; init; } = 1;

    public string Canonical => Kind switch
    {
        MouseSettingKind.Toggle => On ? "On" : "Off",
        MouseSettingKind.Choice => Index.ToString(),
        _ => Number.ToString(),
    };

    public string ValueText => Kind switch
    {
        MouseSettingKind.Toggle => On ? "On" : "Off",
        MouseSettingKind.Choice => Index >= 0 && Index < Options.Count ? Options[Index] : "Unknown",
        _ => Number.ToString(),
    };
}
