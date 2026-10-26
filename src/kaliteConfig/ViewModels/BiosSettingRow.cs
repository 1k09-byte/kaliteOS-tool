using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.ViewModels;

public sealed partial class BiosSettingRow : ObservableObject
{
    internal event Action? StateChanged;

    public BiosSetting Item { get; }

    public BiosSettingRow(BiosSetting item)
    {
        Item = item;
        _valueText = item.Value;
        _sectionText = item.MenuPath.Length == 0
            ? "(unsectioned)"
            : string.Join(" › ", item.MenuPath);
    }

    public string Name => Item.SetupQuestion;
    public string HelpText => Item.HelpString ?? string.Empty;
    private readonly string _sectionText;
    public string SectionText => _sectionText;

    public string TileMetaText
    {
        get
        {
            if (Item.MenuPath.Length > 0) return string.Join(" › ", Item.MenuPath);
            if (!string.IsNullOrEmpty(Item.Token)) return $"Token {Item.Token}";
            if (!string.IsNullOrEmpty(Item.Offset)) return $"Offset {Item.Offset}";
            return string.Empty;
        }
    }
    public string TokenLineText => string.IsNullOrEmpty(Item.Token) ? "-" : $"Token {Item.Token}";
    public string TokenValueText => string.IsNullOrEmpty(Item.Token) ? "-" : Item.Token;
    public string OffsetValueText => string.IsNullOrEmpty(Item.Offset) ? "-" : Item.Offset;
    public string WidthValueText => string.IsNullOrEmpty(Item.Width) ? "-" : Item.Width;
    public string BiosDefaultText => string.IsNullOrEmpty(Item.BiosDefault) ? "-" : Item.BiosDefault;
    public bool IsEnumerated => Item.HasEnumeratedOptions;

    public IReadOnlyList<BiosOption> Options => _options ??= BuildOptions();

    private IReadOnlyList<BiosOption>? _options;

    private static readonly string[] OnLabels = { "Enabled", "On", "True" };
    private static readonly string[] OffLabels = { "Disabled", "Off", "False" };

    private IReadOnlyList<BiosOption> BuildOptions()
    {
        if (Item.Options is null) return Array.Empty<BiosOption>();
        bool binary = LooksLikeBooleanSetting();
        if (!binary) return Item.Options;

        System.Collections.Generic.List<BiosOption> built = new(Item.Options.Count);
        foreach (BiosOption option in Item.Options)
        {
            built.Add(option.Label is null && IsBinaryToken(option.RawToken)
                ? option with { Label = IsOnToken(option.RawToken) ? OnLabels[0] : OffLabels[0] }
                : option);
        }
        return built;
    }

    private bool LooksLikeBooleanSetting()
    {
        foreach (string term in new[] { "enable", "disable" })
        {
            if (Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            if (HelpText.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsBinaryToken(string token)
    {
        string t = token.Trim();
        return t is "0" or "1" or "00" or "01";
    }

    private static bool IsOnToken(string token) => token.Trim() is "1" or "01";

    public bool Matches(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;
        foreach (var term in terms)
            if (!MatchesTerm(term)) return false;
        return true;
    }

    private bool MatchesTerm(string term)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (Name.Contains(term, cmp)) return true;
        if (TileMetaText.Contains(term, cmp)) return true;
        if (Item.Token?.Contains(term, cmp) ?? false) return true;
        if (Item.Offset?.Contains(term, cmp) ?? false) return true;
        if (HelpText.Contains(term, cmp)) return true;
        if (SectionText.Contains(term, cmp)) return true;
        if (Item.Value.Contains(term, cmp)) return true;
        if (ValueDisplay.Contains(term, cmp)) return true;
        foreach (var o in Options)
        {
            if (o.Label?.Contains(term, cmp) ?? false) return true;
            if (o.RawToken.Contains(term, cmp)) return true;
        }
        return false;
    }

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

    public string ValueDisplay
    {
        get
        {
            string value = Item.Value;
            foreach (BiosOption option in Options)
            {
                if (string.Equals(option.RawToken, value, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(option.Label) ? value : option.Label;
            }
            return value;
        }
    }
    public bool IsModified => Item.IsModified;
    public Visibility ModifiedVisibility => IsModified ? Visibility.Visible : Visibility.Collapsed;

    private bool _isPicked;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (_isPicked == value) return;
            _isPicked = value;
            OnPropertyChanged(nameof(IsPicked));
            OnPropertyChanged(nameof(PickedVisibility));
        }
    }

    public Visibility PickedVisibility => _isPicked ? Visibility.Visible : Visibility.Collapsed;

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

    public void ResetToDefault() => ValueText = Item.BiosDefault ?? Item.OriginalValue;

    public void Revert() => ValueText = Item.OriginalValue;
}
