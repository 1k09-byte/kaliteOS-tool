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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages;

/// <summary>
/// The Simple Driver Settings tab: the curated NVCP settings by default, with the
/// wider raw driver space behind an explicit Advanced toggle. All writes go through the
/// one DRS service, so both views share the same backup, save and verify path.
/// </summary>
public sealed partial class SimpleDriverSettingsPage : Page
{
    public SimpleDriverSettingsViewModel Vm { get; } = new();

    private ObservableCollection<NvidiaProfileOption> _pickerItems = new();
    private ObservableCollection<NvidiaPresetStepViewModel> _presetSteps = new();
    private NvidiaSettingPreset? _pendingPreset;

    public SimpleDriverSettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await Vm.RefreshAsync();
            await RefreshProfileIconAsync();
        };
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        Vm.SearchText = sender.Text;
    }

    // ------------------------------------------------------------ profile picker

    /// <summary>
    /// Switching profiles takes a deliberate step through a searchable picker rather
    /// than a stray dropdown, because every edit after this lands on one profile.
    /// </summary>
    private async void ChangeProfile_Click(object sender, RoutedEventArgs e)
    {
        _pickerItems = new ObservableCollection<NvidiaProfileOption>(Vm.Profiles);
        var search = new AutoSuggestBox
        {
            PlaceholderText = "Search programs",
            QueryIcon = new SymbolIcon(Symbol.Find),
        };
        var list = new ListView
        {
            ItemsSource = _pickerItems,
            SelectionMode = ListViewSelectionMode.Single,
            Height = 380,
            ItemTemplate = (DataTemplate)Resources["ProfileOptionTemplate"],
        };
        search.TextChanged += (s, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            string needle = s.Text;
            list.ItemsSource = _pickerItems
                .Where(p => string.IsNullOrWhiteSpace(needle)
                    || p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || p.Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        };

        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(search);
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Choose a profile",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
        };
        var selected = _pickerItems.FirstOrDefault(p => p.ProfileKey == Vm.CurrentProfile?.ProfileKey);
        if (selected is not null) list.SelectedItem = selected;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (list.SelectedItem is NvidiaProfileOption target)
        {
            await Vm.LoadProfileAsync(target);
            await RefreshProfileIconAsync();
        }
    }

    private async Task RefreshProfileIconAsync()
    {
        try
        {
            var current = Vm.CurrentProfile;
            ProfileIcon.Source = current?.Executable is null
                ? null
                : await AppIconService.TryGetIconAsync(current.Executable);
        }
        catch { ProfileIcon.Source = null; }
    }

    // -------------------------------------------------------------------- presets

    /// <summary>
    /// A preset is never applied straight away: the user sees the full plain-English
    /// diff and can uncheck individual settings before anything is staged.
    /// </summary>
    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NvidiaPresetOption option }) return;
        _pendingPreset = option.Preset;

        var resolution = Vm.PreviewPreset(option.Preset);
        _presetSteps = new ObservableCollection<NvidiaPresetStepViewModel>(
            resolution.Steps.Select(s => new NvidiaPresetStepViewModel(s)));

        var list = new ListView
        {
            ItemsSource = _presetSteps,
            SelectionMode = ListViewSelectionMode.None,
            Height = 320,
            ItemTemplate = (DataTemplate)Resources["PresetStepTemplate"],
        };
        var panel = new StackPanel { Spacing = 8, Width = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = option.Description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(list);
        if (resolution.Skipped.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Not included: " + string.Join("; ", resolution.Skipped.Select(s => $"{s.Name} ({s.Reason})")),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = option.Name,
            Content = panel,
            PrimaryButtonText = "Stage these changes",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var excluded = _presetSteps.Where(s => !s.IsIncluded).Select(s => s.Resolution.SettingId).ToHashSet();
        Vm.StagePreset(option.Preset, excluded);
    }

    // ---------------------------------------------------------------------- apply

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Global edits get the same extra confirmation in both the default and the
        // Advanced view, because the reach of the change is the same either way.
        if (Vm.RequiresGlobalConfirmation)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Apply to Global?",
                Content = "These settings affect every application that does not have its own profile. A full driver profile backup is saved first.",
                PrimaryButtonText = "Apply to Global",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await Vm.ApplyCommand.ExecuteAsync(null);
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => Vm.DiscardStagedCommand.Execute(null);

    // ---------------------------------------------------------------------- rows

    private void RawValue_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        if (sender is not FrameworkElement { Tag: NvidiaSettingRowViewModel row }) return;
        if (!row.TryStageRawValue(row.RawInput))
        {
            row.RawInput = $"0x{row.Value:X}";
            Vm.Status = "That is not a value this editor understands. Use hex (0x1F) or a decimal number.";
        }
        e.Handled = true;
    }

    /// <summary>
    /// Jumps to the bundled full editor for one setting, for the power user who wants
    /// the raw DRS view. The bundled tool is a separate process, so it is launched the
    /// same way the Drivers tab already launches it.
    /// </summary>
    private void ShowInFullEditor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NvidiaSettingRowViewModel row }) return;
        try
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "Assets", "nvidiaProfileInspector", "nvidiaProfileInspector.exe");
            if (!File.Exists(exePath)) return;
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch { /* the full editor is optional; failing to launch it is not an error */ }
    }
}

/// <summary>
/// One preset step in the confirmation list, with its include checkbox. A step that
/// could not be resolved is shown greyed and unchecked rather than silently included,
/// so what the user confirms is always exactly what will be staged.
/// </summary>
public sealed partial class NvidiaPresetStepViewModel : ObservableObject
{
    public NvidiaPresetStepResolution Resolution { get; }
    public string SettingId => Resolution.Name;
    public string Detail => Resolution.Resolved
        ? $"{Resolution.FromLabel}  ->  {Resolution.TargetLabel}"
        : $"Not applied - {Resolution.Reason}";
    public bool IsSkipped => !Resolution.Resolved;

    /// <summary>Only a resolved step can be included; a skipped one is locked off.</summary>
    public bool CanInclude => Resolution.Resolved;

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    public NvidiaPresetStepViewModel(NvidiaPresetStepResolution resolution)
    {
        Resolution = resolution;
        IsIncluded = resolution.Resolved;
    }
}
