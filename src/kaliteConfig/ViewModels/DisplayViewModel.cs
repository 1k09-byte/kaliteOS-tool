// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

public sealed partial class DisplayViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private ObservableCollection<DisplayInfo> _displays = new();

    [ObservableProperty]
    private DisplayInfo? _selectedDisplay;

    [ObservableProperty]
    private ObservableCollection<DisplayMode> _availableResolutions = new();

    [ObservableProperty]
    private DisplayMode? _selectedResolution;

    [ObservableProperty]
    private ObservableCollection<DisplayMode> _availableRefreshRates = new();

    [ObservableProperty]
    private DisplayMode? _selectedRefreshRate;

    [ObservableProperty]
    private ObservableCollection<int> _availableScales = new();

    [ObservableProperty]
    private int _selectedScale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleIsSupported))]
    private bool _isScaleSupported;

    [ObservableProperty]
    private ObservableCollection<RotationOption> _availableRotations = new(new[]
    {
        new RotationOption("Landscape", 0),
        new RotationOption("Portrait", 90),
        new RotationOption("Landscape (Flipped)", 180),
        new RotationOption("Portrait (Flipped)", 270)
    });

    [ObservableProperty]
    private RotationOption? _selectedRotation;

    [ObservableProperty]
    private string _hdrStatusText = "Checking...";

    [ObservableProperty]
    private bool _isHdrEnabled;

    [ObservableProperty]
    private bool _isHdrSupported;

    [ObservableProperty]
    private bool _isAwaitingConfirmation;

    [ObservableProperty]
    private string _countdownText = "";

    private bool _isProgrammaticChange;
    private List<DisplayMode> _allModes = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public ICommand RefreshCommand { get; }
    public ICommand RevertNowCommand { get; }
    public ICommand ConfirmChangesCommand { get; }

    /// <summary>
    /// False only when the OS refused the DPI-scale packet, so the picker can be disabled
    /// with a reason instead of offering a control that silently does nothing.
    /// </summary>
    public bool ScaleIsSupported => IsScaleSupported;

    public DisplayViewModel()
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        RefreshCommand = new RelayCommand(Refresh);
        RevertNowCommand = new RelayCommand(() => DisplayRevertService.RevertNow());
        ConfirmChangesCommand = new RelayCommand(() => DisplayRevertService.Confirm());

        DisplayRevertService.StateChanged += OnRevertStateChanged;
        DisplayRevertService.CountdownTicked += OnCountdownTicked;

        Refresh();
    }

    public void Refresh()
    {
        // Keep the same panel selected across a refresh. Re-selecting the first entry would
        // move the picker back to display 1 after every change, which is confusing when the
        // change was made on a later display.
        var keep = SelectedDisplay?.DeviceName;

        var newDisplays = DisplayEnumerationService.Enumerate();
        Displays.Clear();
        foreach (var d in newDisplays) Displays.Add(d);

        SelectedDisplay = keep is null
            ? Displays.FirstOrDefault()
            : Displays.FirstOrDefault(d => d.DeviceName == keep) ?? Displays.FirstOrDefault();
    }

    partial void OnSelectedDisplayChanged(DisplayInfo? value)
    {
        if (value == null) return;
        _isProgrammaticChange = true;

        _allModes = DisplaySettingsService.GetAvailableModes(value.DeviceName);

        // Populate Resolutions (deduplicated by WxH)
        var uniqueRes = _allModes.GroupBy(m => m.ResolutionKey).Select(g => g.First()).ToList();
        AvailableResolutions.Clear();
        foreach (var r in uniqueRes) AvailableResolutions.Add(r);

        SelectedResolution = AvailableResolutions.FirstOrDefault(r => r.Width == value.CurrentWidth && r.Height == value.CurrentHeight);

        // Scale
        AvailableScales.Clear();
        foreach (var s in value.AvailableScalePercents) AvailableScales.Add(s);
        if (AvailableScales.Count == 0) AvailableScales.Add(value.CurrentScalePercent);
        IsScaleSupported = value.IsScaleSupported;
        SelectedScale = value.CurrentScalePercent;

        // Rotation
        SelectedRotation = AvailableRotations.FirstOrDefault(r => r.Value == value.CurrentRotationDegrees) ?? AvailableRotations[0];

        // HDR
        var hdrState = DisplayHdrService.GetHdrState(value.AdapterId, value.CcdTargetId);
        IsHdrSupported = hdrState == DisplayHdrService.HdrState.Enabled || hdrState == DisplayHdrService.HdrState.Disabled;
        IsHdrEnabled = hdrState == DisplayHdrService.HdrState.Enabled;
        
        HdrStatusText = hdrState switch
        {
            DisplayHdrService.HdrState.Enabled => "On",
            DisplayHdrService.HdrState.Disabled => "Off",
            DisplayHdrService.HdrState.ForceDisabled => "Disabled by policy",
            _ => "Not supported by this display"
        };

        _isProgrammaticChange = false;
        UpdateRefreshRatesForResolution();
    }

    partial void OnSelectedResolutionChanged(DisplayMode? value)
    {
        if (_isProgrammaticChange || value == null || SelectedDisplay == null) return;
        UpdateRefreshRatesForResolution();
        ApplyToDisplay();
    }

    partial void OnSelectedRefreshRateChanged(DisplayMode? value)
    {
        if (_isProgrammaticChange || value == null || SelectedDisplay == null) return;
        ApplyToDisplay();
    }

    partial void OnSelectedRotationChanged(RotationOption? value)
    {
        if (_isProgrammaticChange || value == null || SelectedDisplay == null) return;
        ApplyToDisplay();
    }

    partial void OnSelectedScaleChanged(int value)
    {
        if (_isProgrammaticChange || SelectedDisplay == null || !IsScaleSupported) return;
        if (value == SelectedDisplay.CurrentScalePercent) return;

        // Scale is not a mode change, so it is applied immediately rather than going
        // through the mode revert window: there is no intermediate state to roll back to.
        if (!DisplayScaleService.Write(SelectedDisplay.SourceAdapterId, SelectedDisplay.CcdSourceId, value))
        {
            // The OS refused it, so put the picker back on the value the desktop still has.
            _isProgrammaticChange = true;
            SelectedScale = SelectedDisplay.CurrentScalePercent;
            _isProgrammaticChange = false;
            return;
        }

        Refresh();
    }

    partial void OnIsHdrEnabledChanged(bool value)
    {
        if (_isProgrammaticChange || SelectedDisplay == null || !IsHdrSupported) return;
        DisplayHdrService.SetHdrEnabled(SelectedDisplay.AdapterId, SelectedDisplay.CcdTargetId, value);
        Refresh(); // Re-read state
    }

    private void UpdateRefreshRatesForResolution()
    {
        if (SelectedResolution == null || SelectedDisplay == null) return;
        
        _isProgrammaticChange = true;
        
        var matchingRates = _allModes.Where(m => m.Width == SelectedResolution.Width && m.Height == SelectedResolution.Height).ToList();
        AvailableRefreshRates.Clear();
        foreach (var r in matchingRates) AvailableRefreshRates.Add(r);

        SelectedRefreshRate = AvailableRefreshRates.OrderBy(r => Math.Abs(r.RefreshRate - SelectedDisplay.CurrentRefreshRate)).FirstOrDefault();
        
        _isProgrammaticChange = false;
    }

    private void ApplyToDisplay()
    {
        if (SelectedDisplay == null || SelectedRefreshRate == null || SelectedRotation == null) return;

        DisplayRevertService.CaptureAnchor();

        // Convert rotation degrees to DMDO constant
        int rotConst = SelectedRotation.Value switch
        {
            90 => 1,
            180 => 2,
            270 => 3,
            _ => 0
        };

        bool ok = DisplaySettingsService.ApplyMode(SelectedDisplay.DeviceName, SelectedRefreshRate, rotConst);
        
        if (ok)
        {
            DisplayRevertService.StartCountdown(15);
            // Don't auto-refresh here, let the user confirm first so the UI doesn't jump
        }
    }

    public void TryApplyArrangement()
    {
        DisplayRevertService.CaptureAnchor();
        if (DisplayArrangementService.ApplyArrangement(Displays))
        {
            DisplayRevertService.StartCountdown(15);
        }
    }

    private void OnRevertStateChanged()
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            IsAwaitingConfirmation = DisplayRevertService.IsAwaitingConfirmation;
            if (!IsAwaitingConfirmation) Refresh(); // Refresh when countdown ends (confirmed or reverted)
        });
    }

    private void OnCountdownTicked(int seconds)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            CountdownText = $"Reverting in {seconds}s";
        });
    }

    public void Dispose()
    {
        DisplayRevertService.StateChanged -= OnRevertStateChanged;
        DisplayRevertService.CountdownTicked -= OnCountdownTicked;
    }
}

public record RotationOption(string Name, int Value);
