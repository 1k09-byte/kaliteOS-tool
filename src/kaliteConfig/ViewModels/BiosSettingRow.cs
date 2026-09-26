// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using kaliteConfig.Models;
using System;
using System.Collections.Generic;

namespace kaliteConfig.ViewModels;

/// <summary>
/// Observable wrapper around one <see cref="BiosSetting"/> for list/detail
/// binding. Edits flow: control → row (ValueText / SelectedToken) → model,
/// and validation/modified state recomputes immediately.
/// </summary>
public sealed partial class BiosSettingRow : ObservableObject
{
    /// <summary>Raised whenever modified/validation state changed (for counts).</summary>
    internal event Action? StateChanged;

    public BiosSetting Item { get; }

    public BiosSettingRow(BiosSetting item)
    {
        Item = item;
        _valueText = item.Value;
    }

    // ---- display fields (immutable per document) ----

    public string Name => Item.SetupQuestion;
    public string HelpText => Item.HelpString ?? string.Empty;
    public string SectionText => Item.MenuPath.Length == 0
        ? "(unsectioned)"
        : string.Join(" › ", Item.MenuPath);
    public string TokenLineText => string.IsNullOrEmpty(Item.Token) ? "-" : $"Token {Item.Token}";
    public string TokenValueText => string.IsNullOrEmpty(Item.Token) ? "-" : Item.Token;
    public string OffsetValueText => string.IsNullOrEmpty(Item.Offset) ? "-" : Item.Offset;
    public string WidthValueText => string.IsNullOrEmpty(Item.Width) ? "-" : Item.Width;
    public string BiosDefaultText => string.IsNullOrEmpty(Item.BiosDefault) ? "-" : Item.BiosDefault;
    public bool IsEnumerated => Item.HasEnumeratedOptions;
    public IReadOnlyList<BiosOption> Options => Item.Options ?? Array.Empty<BiosOption>();

    /// <summary>Matches a search query against name, token, help text and current value.</summary>
    public bool Matches(string query)
    {
        return Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (Item.Token?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               HelpText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Item.Value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ---- editable value (TextBox path: free-form/numeric items) ----

    private string _valueText;
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value) return;
            _valueText = value;
            Item.Value = value.Trim();
            OnPropertyChanged(nameof(ValueText));
            RefreshState();
        }
    }

    // ---- editable value (ComboBox path: enumerated items) ----

    public string SelectedToken
    {
        get => Item.Value;
        set
        {
            var token = value ?? "";
            if (string.Equals(Item.Value, token, StringComparison.Ordinal)) return;
            Item.Value = token;
            _valueText = token;
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(SelectedToken));
            RefreshState();
        }
    }

    // ---- state ----

    public string ValueDisplay => Item.Value;
    public bool IsModified => Item.IsModified;
    public Visibility ModifiedVisibility => IsModified ? Visibility.Visible : Visibility.Collapsed;

    private bool _isInvalid;
    public bool IsInvalid
    {
        get => _isInvalid;
        private set { if (_isInvalid != value) { _isInvalid = value; OnPropertyChanged(); OnPropertyChanged(nameof(ErrorVisibility)); } }
    }

    private string _validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => _validationMessage;
        private set { if (_validationMessage != value) { _validationMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(ErrorVisibility)); } }
    }

    public Visibility ErrorVisibility => IsInvalid ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshState()
    {
        var message = Item.ValidateValue();
        IsInvalid = message is not null;
        ValidationMessage = message ?? string.Empty;
        OnPropertyChanged(nameof(ValueDisplay));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(ModifiedVisibility));
        StateChanged?.Invoke();
    }

    /// <summary>Per-item "reset to BIOS default" (falls back to the imported value).</summary>
    public void ResetToDefault() => ValueText = Item.BiosDefault ?? Item.OriginalValue;

    /// <summary>Revert to the value from the imported file.</summary>
    public void Revert() => ValueText = Item.OriginalValue;
}
