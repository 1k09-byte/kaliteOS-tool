// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace kaliteConfig.ViewModels;

/// <summary>One row of the pre-export review list: name → old value → new value.</summary>
public sealed record ChangeSummary(string Name, string PathText, string OldValue, string NewValue);

/// <summary>
/// A settings-category entry for the left nav (WinUI Settings-style).
/// Either a keyword bucket ("CPU", "PCIe", "Security"…) or the special
/// "All settings" / "Modified" views. Rows are recomputed per document.
/// </summary>
public sealed class BiosCategory
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public int Count { get; init; }
    // "All settings" and "Modified" don't carry static counts (Modified changes
    // live); only keyword buckets show a badge.
    public string CountText => Count > 0 ? $"({Count})" : "";

    public static readonly BiosCategory All = new() { Key = "All", DisplayName = "All settings" };
    public static readonly BiosCategory Modified = new() { Key = "Modified", DisplayName = "Modified" };
}

/// <summary>
/// Drives the BIOS Manager page: import a SCEWIN dump, browse the menu tree,
/// filter settings, edit values with per-item reset, review a change list and
/// export a SCEWIN-compatible file. Parsing and filtering run off the UI
/// thread; results are marshaled back via <see cref="DispatcherQueue"/>.
/// </summary>
public sealed partial class BiosManagerViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private ScewinDocument? _document;
    private List<BiosSettingRow> _allRows = new();
    private BiosMenuSection? _selectedSection;
    private CancellationTokenSource? _filterCts;
    private string _searchText = string.Empty;

    public BiosManagerViewModel()
    {
        _dispatcher = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    // ---- observable state ----

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string LoadingText { get; set; } = "Loading…";

    [ObservableProperty]
    public partial bool HasDocument { get; set; }

    [ObservableProperty]
    public partial string FileNameText { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial BiosSettingRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial int ModifiedCount { get; set; }

    public ObservableCollection<BiosMenuSection> Sections { get; } = new();

    public ObservableCollection<BiosCategory> Categories { get; } = new();

    [ObservableProperty]
    public partial ObservableCollection<BiosSettingRow> Rows { get; set; } = new();

    // ---- derived state ----

    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasChanges => ModifiedCount > 0;
    public string ModifiedCountText => ModifiedCount == 0 ? "No changes" : $"{ModifiedCount} modified";

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => !IsLoading && !HasDocument ? Visibility.Visible : Visibility.Collapsed;
    public bool CanExportNow => !IsLoading;
    /// <summary>Search/section filter returned zero rows while a document is loaded.</summary>
    public bool HasNoResults => HasDocument && Rows.Count == 0;
    public Visibility NoResultsVisibility => HasNoResults ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => HasNoResults ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Flat dump (no section headers): the nav tree would be a single useless node.</summary>
    public bool HasSections => Sections.Count > 1 || (Sections.Count == 1 && Sections[0].Children.Count > 0);
    public Visibility TreeVisibility => HasSections ? Visibility.Visible : Visibility.Collapsed;
    public GridLength TreeColumnWidth => HasSections ? new GridLength(260) : new GridLength(0);
    public Visibility DetailVisibility => SelectedRow is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDetailVisibility => SelectedRow is not null ? Visibility.Collapsed : Visibility.Visible;

    public bool EditingIsEnumerated => SelectedRow?.IsEnumerated ?? false;
    public Visibility EditingEnumeratedVisibility => EditingIsEnumerated ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditingFreeformVisibility => EditingIsEnumerated ? Visibility.Collapsed : Visibility.Visible;

    // Read-only now: the ComboBox writes back via code-behind (a TwoWay
    // binding could push an empty value while the picker repopulates).
    public string EditingSelectedToken => SelectedRow?.SelectedToken ?? "";

    [ObservableProperty]
    public partial bool ShowOnlyChanges { get; set; }

    [ObservableProperty]
    public partial BiosCategory? SelectedCategoryItem { get; set; } = BiosCategory.All;

    partial void OnSelectedCategoryItemChanged(BiosCategory? value) => ApplyFilterNow();

    public string EditingValueText
    {
        get => SelectedRow?.ValueText ?? "";
        set { if (SelectedRow is not null) SelectedRow.ValueText = value; }
    }

    public string EditingValidationMessage => SelectedRow?.ValidationMessage ?? "";
    public Visibility EditingErrorVisibility => SelectedRow?.IsInvalid == true ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(LoadingVisibility));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(CanExportNow));
    }

    partial void OnHasDocumentChanged(bool value) => OnPropertyChanged(nameof(EmptyVisibility));
    partial void OnSelectedRowChanged(BiosSettingRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(NoDetailVisibility));
        OnPropertyChanged(nameof(EditingIsEnumerated));
        OnPropertyChanged(nameof(EditingEnumeratedVisibility));
        OnPropertyChanged(nameof(EditingFreeformVisibility));
        OnPropertyChanged(nameof(EditingSelectedToken));
        OnPropertyChanged(nameof(EditingValueText));
        OnPropertyChanged(nameof(EditingValidationMessage));
        OnPropertyChanged(nameof(EditingErrorVisibility));
    }

    partial void OnModifiedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ModifiedCountText));
        OnPropertyChanged(nameof(HasChanges));
    }

    // ---- commands ----

    private bool _autoLoadStarted;

    /// <summary>
    /// Called when the page opens: without touching the UI, export the live
    /// BIOS settings with the bundled SCEWIN tool and show them. A cached dump
    /// from an earlier run is shown instantly while a fresh export runs.
    /// </summary>
    public async Task AutoLoadAsync()
    {
        if (_autoLoadStarted || IsLoading) return;
        _autoLoadStarted = true;

        // Instant content: replay the cached dump if one exists.
        if (ScewinExportService.HasCachedDump && !HasDocument)
        {
            await LoadPathAsync(ScewinExportService.DumpPath, "cached");
        }

        if (!ScewinExportService.IsAvailable)
        {
            if (!HasDocument)
            {
                ShowError("SCEWIN_64.exe was not found next to the app. " +
                          "Use “Load dump” to open a dump file manually.");
            }
            return;
        }

        await ExportAndLoadCommand.ExecuteAsync(null);
    }

    /// <summary>Run the bundled SCEWIN tool and parse its dump (background thread).</summary>
    [RelayCommand]
    private async Task ExportAndLoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ShowError(null);
        try
        {
            LoadingText = "Exporting BIOS settings with SCEWIN…";
            var exporter = new ScewinExportService();
            var path = await Task.Run(() => exporter.ExportAsync());

            LoadingText = "Parsing BIOS settings…";
            await LoadPathAsync(path);
        }
        catch (Exception ex)
        {
            ShowError($"SCEWIN export failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Parse an already-known dump file (background thread) and show it.</summary>
    private async Task LoadPathAsync(string path, string? tag = null)
    {
        var text = await Task.Run(() => File.ReadAllText(path));
        var doc = await Task.Run(() => ScewinParser.Parse(text, Path.GetFileName(path), new UiProgress(_dispatcher, this)));
        OnDocumentLoaded(doc);
        StatusText = (tag is null ? "" : $"[{tag}] ") +
            $"Loaded {doc.Items.Count:N0} settings from {doc.SourceName}.";
    }

    /// <summary>Pick and parse a SCEWIN dump on a background thread.</summary>
    [RelayCommand]
    private async Task LoadFileAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ShowError(null);
        try
        {
            var path = await PickDumpFileAsync();
            if (path is null) return;

            LoadingText = $"Reading {Path.GetFileName(path)}…";
            await LoadPathAsync(path);
        }
        catch (ScewinParseException ex)
        {
            ShowError($"Not a SCEWIN dump: {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowError($"Could not load file: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Export the document (edited Value lines only) to a new .txt.</summary>
    [RelayCommand]
    private async Task SaveFileAsync()
    {
        if (_document is null) return;
        try
        {
            var text = ScewinExporter.Export(_document);
            var path = await PickSaveFileAsync(DefaultExportName());
            if (path is null) return;

            await File.WriteAllTextAsync(path, text);
            StatusText = $"Exported {ModifiedCount} change(s) → {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Apply changes directly to the BIOS via SCEWIN.</summary>
    [RelayCommand]
    private async Task ApplyToBiosAsync()
    {
        if (_document is null || !HasChanges) return;
        
        IsLoading = true;
        ShowError(null);
        try
        {
            LoadingText = "Writing changes to BIOS…";
            var text = ScewinExporter.Export(_document);
            var tempPath = Path.Combine(Path.GetTempPath(), "kaliteConfig_BIOS_Changes.txt");
            await File.WriteAllTextAsync(tempPath, text);

            var service = new ScewinExportService();
            await Task.Run(() => service.ImportAsync(tempPath));
            
            try { File.Delete(tempPath); } catch { }
            
            StatusText = $"Successfully applied {ModifiedCount} change(s) to BIOS.";
            
            // Re-export from BIOS to refresh the UI and confirm changes actually stuck
            await ExportAndLoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to apply changes to BIOS: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Debounced search filter (runs the match off the UI thread).</summary>
    [RelayCommand]
    private Task FilterAsync(string? text)
    {
        _searchText = text ?? "";
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        var ct = _filterCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ct);
                var rows = ComputeFilteredRows();
                _dispatcher.TryEnqueue(() =>
                {
                    if (!ct.IsCancellationRequested) Rows = new ObservableCollection<BiosSettingRow>(rows);
                });
            }
            catch (OperationCanceledException) { /* newer filter superseded this one */ }
        });
    }

    /// <summary>Filter the list to a menu section (null/All → every setting).</summary>
    public void SelectSection(BiosMenuSection? section)
    {
        _selectedSection = section ?? _document?.RootSection;
        ApplyFilterNow();
    }

    partial void OnRowsChanged(ObservableCollection<BiosSettingRow> value)
        => OnPropertyChanged(nameof(HasNoResults));

    /// <summary>Per-item reset to the BIOS default value.</summary>
    [RelayCommand]
    private void ResetSetting(BiosSettingRow? row) => row?.ResetToDefault();

    /// <summary>Revert every changed setting back to its imported value.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        foreach (var row in _allRows.Where(r => r.IsModified)) row.Revert();
        StatusText = "All changes reverted.";
    }

    /// <summary>Name → old → new list for the pre-export review dialog.</summary>
    public List<ChangeSummary> GetChanges() =>
        _allRows.Where(r => r.IsModified)
                .Select(r => new ChangeSummary(r.Name, r.SectionText, r.Item.OriginalValue, r.Item.Value))
                .ToList();

    /// <summary>Items whose new value would be invalid, with reasons (non-blocking warnings).</summary>
    public List<(BiosSettingRow Row, string Message)> ValidateChanges() =>
        _allRows.Where(r => r.IsInvalid)
                .Select(r => (r, r.ValidationMessage))
                .ToList();

    // ---- internals ----

    private void OnDocumentLoaded(ScewinDocument doc)
    {
        _document = doc;
        _filterCts?.Cancel();

        foreach (var row in _allRows) row.StateChanged -= OnRowStateChanged;
        _allRows = doc.Items.Select(i => new BiosSettingRow(i)).ToList();
        foreach (var row in _allRows) row.StateChanged += OnRowStateChanged;

        Sections.Clear();
        Sections.Add(doc.RootSection);

        HasDocument = true;
        FileNameText = doc.SourceName;
        SummaryText = $"{doc.Items.Count:N0} settings in {CountSections(doc.RootSection):N0} menu sections";
        SelectedRow = null;
        _selectedSection = doc.RootSection;

        Rows = new ObservableCollection<BiosSettingRow>(_allRows);
        RebuildCategories();
        SelectedCategoryItem = BiosCategory.All;
        _selectedSection = null;
        UpdateModifiedCount();
    }

    private static int CountSections(BiosMenuSection root) =>
        root.SelfAndDescendants().Count(s => !s.IsRoot);

    private void OnRowStateChanged() => UpdateModifiedCount();

    private void UpdateModifiedCount() => ModifiedCount = _allRows.Count(r => r.IsModified);

    private List<BiosSettingRow> ComputeFilteredRows()
    {
        IEnumerable<BiosSettingRow> rows = _allRows;

        if (SelectedCategoryItem is { } category &&
            !ReferenceEquals(category, BiosCategory.All))
        {
            rows = ReferenceEquals(category, BiosCategory.Modified)
                ? rows.Where(r => r.IsModified)
                : rows.Where(r => CategoryKeywords.TryGetValue(category.Key, out var terms) &&
                                  terms.Any(t => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                                 r.SectionText.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        else if (ShowOnlyChanges)
        {
            rows = rows.Where(r => r.IsModified);
        }

        var baseRows = rows.ToList();
        var query = _searchText.Trim();
        if (query.Length == 0) return baseRows;
        return baseRows.Where(r => r.Matches(query)).ToList();
    }

    /// <summary>
    /// Keyword buckets that map dump setting names/paths to friendly category
    /// names (like the BIOS's own menu: CPU, PCIe, Security…). Evaluated per
    /// document so the counts always match what the filter will show.
    /// </summary>
    public static readonly Dictionary<string, string[]> CategoryKeywords = new()
    {
        ["CPU"] = new[] { "cpu", "processor", "core", "smt", "c-state", "cstate", "pstate", "cppc", "smt" },
        ["Memory"] = new[] { "memory", "dram", "ddr", "mrc", "mem " },
        ["PCIe"] = new[] { "pcie", "pci express", "pci ", "slot", "link speed", "lane", "aspm" },
        ["Graphics"] = new[] { "gfx", "gpu", "igpu", "display", "video", "dGPU", "iGPU", "apu" },
        ["USB"] = new[] { "usb" },
        ["Storage"] = new[] { "sata", "nvme", "m.2", "storage", "raid", "ahci", "ide" },
        ["Networking"] = new[] { "wifi", "wlan", "network", "lan", "ethernet", "bluetooth" },
        ["Audio"] = new[] { "audio", "hda", "sound", "azalia" },
        ["Thunderbolt"] = new[] { "thunderbolt", "tbt" },
        ["Security"] = new[] { "security", "secure boot", "tpm", "fTPM", "password", "aes", "encrypt", "txt", "sgx", "pki", "vault" },
        ["Power"] = new[] { "power", "voltage", "overclock", "oc ", "fan", "thermal", "temperature", "acpi", "battery", "clock power" },
        ["Boot"] = new[] { "boot", "uefi", "csm", "legacy" },
    };

    /// <summary>Builds the category list (with live counts) for the loaded document.</summary>
    private void RebuildCategories()
    {
        Categories.Clear();
        Categories.Add(BiosCategory.All);
        Categories.Add(BiosCategory.Modified);

        foreach (var (key, terms) in CategoryKeywords)
        {
            var count = _allRows.Count(r =>
                terms.Any(t => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               r.SectionText.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (count > 0)
            {
                Categories.Add(new BiosCategory { Key = key, DisplayName = key, Count = count });
            }
        }
    }

    private void ApplyFilterNow() => Rows = new ObservableCollection<BiosSettingRow>(ComputeFilteredRows());

    private void ShowError(string? message)
    {
        ErrorText = message ?? "";
        // Re-trigger the InfoBar even if it's already open (user may have closed it).
        if (HasError) HasError = false;
        HasError = message is not null;
    }

    private string DefaultExportName()
    {
        var name = _document?.SourceName ?? "scewin_dump.txt";
        return Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem
            ? $"{stem}_modified.txt"
            : "scewin_modified.txt";
    }

    private static nint Hwnd =>
        App.MainWindow is { } window ? WindowNative.GetWindowHandle(window) : 0;

    /// <summary>
    /// WinUI 3 desktop picker pattern (InitializeWithWindow + window handle).
    /// Falls back to the Win32 IFileDialog when the app runs elevated, where
    /// the brokered pickers throw E_ACCESSDENIED.
    /// </summary>
    private static async Task<string?> PickDumpFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, Hwnd);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch
        {
            return Win32FilePicker.PickOpenFile(Hwnd);
        }
    }

    private static async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = suggestedName,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
            InitializeWithWindow.Initialize(picker, Hwnd);
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch
        {
            return Win32FilePicker.PickSaveFile(Hwnd, suggestedName);
        }
    }

    /// <summary>Marshals parse progress to the UI thread without relying on SynchronizationContext.</summary>
    private sealed class UiProgress : IProgress<double>
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly BiosManagerViewModel _viewModel;
        private int _lastPercent;

        public UiProgress(DispatcherQueue dispatcher, BiosManagerViewModel viewModel)
        {
            _dispatcher = dispatcher;
            _viewModel = viewModel;
        }

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(value, 0, 100);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            _dispatcher.TryEnqueue(() =>
            {
                if (_viewModel.IsLoading)
                    _viewModel.LoadingText = $"Parsing BIOS settings… {percent}%";
            });
        }
    }
}
