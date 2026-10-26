using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using kaliteConfig.Input;

namespace kaliteConfig.ViewModels;

public sealed partial class MouseSettingRow : ObservableObject
{
    public MouseSettingRow(Input.MouseSettingSpec spec)
    {
        Spec = spec;
        Options = spec.Options;
        OriginalText = spec.ValueText;
        OriginalCanonical = spec.Canonical;
        IsOn = spec.On;
        SelectedIndex = spec.Index;
        Number = spec.Number;
    }

    public Input.MouseSettingSpec Spec { get; }
    public string Id => Spec.Id;
    public string Label => Spec.Label;
    public string Hint => Spec.Hint;
    public bool HasHint => Spec.Hint.Length > 0;
    public bool IsToggle => Spec.Kind == Input.MouseSettingKind.Toggle;
    public bool IsChoice => Spec.Kind == Input.MouseSettingKind.Choice;
    public bool IsNumber => Spec.Kind == Input.MouseSettingKind.Number;
    public double Minimum => Spec.Minimum;
    public double Maximum => Spec.Maximum;
    public double Step => Spec.Step;
    public IReadOnlyList<string> Options { get; }
    public string OriginalText { get; }
    public string OriginalCanonical { get; }
    public bool IsButtonSetting => string.Equals(Spec.Group, Input.MouseSettingGroup.Buttons, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial double Number { get; set; }

    public string Canonical => Spec.Kind switch
    {
        Input.MouseSettingKind.Toggle => IsOn ? "On" : "Off",
        Input.MouseSettingKind.Choice => SelectedIndex.ToString(),
        _ => ((int)Math.Round(Number)).ToString(),
    };

    public string StageText => Canonical == OriginalCanonical
        ? OriginalText
        : (Spec.Kind == Input.MouseSettingKind.Choice
            ? (SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : Canonical)
            : Canonical)
          + " (was " + OriginalText + ")";
}
