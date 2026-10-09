// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

/// <summary>
/// Drives the NVIDIA display panel.
///
/// Edits happen against a draft copy, never the live profile, so a slider drag cannot
/// hammer the driver with hundreds of writes. Apply is explicit, and every Apply is
/// bracketed by a revert window: if the panel is left looking wrong (or the machine ends
/// up in a state the user cannot undo quickly), the previous values come back on their own.
/// </summary>
public sealed partial class NvidiaSettingsViewModel : ObservableObject
{
    private const int RevertWindowSeconds = 15;

    /// <summary>
    /// The value a slider holds when the monitor implements the control but would not
    /// report its current one. Until the user moves the slider it is not a real reading,
    /// so it is never written back.
    /// </summary>
    private const double UnreadPlaceholder = 0.5;

    private static string ProfileStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "Nvidia", "display-profiles.json");

    private readonly Dictionary<string, NvidiaDisplayProfile> _savedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private System.Threading.Timer? _revertTimer;
    private NvidiaDisplayProfile? _anchor;
    private bool _syncing;
    private int _secondsRemaining;

    [ObservableProperty]
    public partial ObservableCollection<NvidiaDisplayProfile> Displays { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SupportsDigitalVibrance))]
    [NotifyPropertyChangedFor(nameof(SupportsBrightness))]
    [NotifyPropertyChangedFor(nameof(SupportsContrast))]
    [NotifyPropertyChangedFor(nameof(SupportsGamma))]
    [NotifyPropertyChangedFor(nameof(SupportsHue))]
    [NotifyPropertyChangedFor(nameof(SupportsScaling))]
    [NotifyPropertyChangedFor(nameof(SupportsColorData))]
    [NotifyPropertyChangedFor(nameof(HdrStatusNote))]
    [NotifyPropertyChangedFor(nameof(SupportsColorGain))]
    [NotifyPropertyChangedFor(nameof(SupportsHdr))]
    [NotifyPropertyChangedFor(nameof(SupportsAnyControl))]
    [NotifyPropertyChangedFor(nameof(ColorNote))]
    [NotifyPropertyChangedFor(nameof(BrightnessText))]
    [NotifyPropertyChangedFor(nameof(ContrastText))]
    public partial NvidiaDisplayProfile? SelectedDisplay { get; set; }

    // ── draft values ──
    [ObservableProperty]
    public partial double DigitalVibrance { get; set; }
    [ObservableProperty]
    public partial double Brightness { get; set; }
    [ObservableProperty]
    public partial double Contrast { get; set; }
    [ObservableProperty]
    public partial double Gamma { get; set; } = 1.0;
    [ObservableProperty]
    public partial double Hue { get; set; }
    [ObservableProperty]
    public partial bool HdrEnabled { get; set; }
    [ObservableProperty]
    public partial double RedGain { get; set; }
    [ObservableProperty]
    public partial double GreenGain { get; set; }
    [ObservableProperty]
    public partial double BlueGain { get; set; }

    // The five choice fields are surfaced as indexes rather than enums: the controls that
    // drive them (RadioButtons, ComboBox) all speak SelectedIndex, and routing through an
    // index keeps the binding one-way-simple in both directions.
    private NvidiaScalingLocation _scalingLocation;
    private NvidiaScalingMode _scalingMode;
    private NvidiaColorDepthOption _colorDepth;
    private NvidiaColorFormatOption _colorFormat;
    private NvidiaDynamicRangeOption _dynamicRange;

    [ObservableProperty]
    public partial int ScalingLocationIndex { get; set; }
    [ObservableProperty]
    public partial int ScalingModeIndex { get; set; }
    [ObservableProperty]
    public partial int ColorDepthIndex { get; set; }
    [ObservableProperty]
    public partial int ColorFormatIndex { get; set; }
    [ObservableProperty]
    public partial DynamicRangeChoice? DynamicRangeChoice { get; set; }

    public IReadOnlyList<string> ScalingLocationLabels { get; } = new[] { "Display", "GPU" };
    public IReadOnlyList<string> ColorDepthLabels { get; } = new[] { "8 bpc (RGB)", "10 bpc (RGB)" };
    public IReadOnlyList<string> ColorFormatLabels { get; } = NvidiaColorFormatLabels.All;
    private IReadOnlyList<DynamicRangeChoice> _dynamicRangeChoices = BuildDynamicRangeChoices(NvidiaColorFormatOption.Rgb);

    public ObservableCollection<DynamicRangeChoice> DynamicRangeChoices { get; } = new ObservableCollection<DynamicRangeChoice>();

    private static IReadOnlyList<DynamicRangeChoice> BuildDynamicRangeChoices(NvidiaColorFormatOption format) =>
        NvidiaColorMap.ReachableRanges(format)
            .Select(DynamicRangeChoice.For)
            .ToArray();

    /// <summary>
    /// Rebuilds the dynamic-range list with the selection dropped first, for the
    /// same reason as <see cref="SetScalingModes"/>: a TwoWay SelectedItem is a
    /// CollectionChanged listener, and clearing the items under a live selection
    /// makes the ComboBox hand WinRT a container it no longer owns
    /// (E_INVALIDARG -> "The parameter is incorrect. (parameter 'container')").
    /// </summary>
    private void PopulateDynamicRangeChoices(IReadOnlyList<DynamicRangeChoice> newChoices)
    {
        _rebinding = true;
        try
        {
            if (DynamicRangeChoice is not null) DynamicRangeChoice = null;

            DynamicRangeChoices.Clear();
            foreach (var choice in newChoices)
            {
                DynamicRangeChoices.Add(choice);
            }
        }
        finally
        {
            _rebinding = false;
        }
    }

    private DynamicRangeChoice? RangeChoiceFor(NvidiaDynamicRangeOption option) =>
        _dynamicRangeChoices.FirstOrDefault(choice => choice.Option == option);

    private IReadOnlyList<NvidiaScalingMode> _scalingModes = NvidiaScalingMap.ModesFor(NvidiaScalingLocation.Display);
    public ObservableCollection<string> ScalingModeChoices { get; } = new ObservableCollection<string>();

    /// <summary>
    /// True while a selector's item list is being rebuilt. The TwoWay bindings
    /// on <c>SelectedIndex</c>/<c>SelectedItem</c> fire CollectionChanged while
    /// a Clear/Add is in progress and push a stale selection straight back into
    /// this ViewModel; those push-backs are dropped rather than acted on.
    /// </summary>
    private bool _rebinding;

    /// <summary>Position of <paramref name="mode"/> in the current list, or -1 when it is not offered.</summary>
    private int ScalingModeIndexFor(NvidiaScalingMode mode)
    {
        for (int i = 0; i < _scalingModes.Count; i++)
            if (_scalingModes[i] == mode) return i;
        return -1;
    }

    /// <summary>
    /// Points the list at <paramref name="location"/>'s modes and selects the given one.
    ///
    /// The selection is dropped BEFORE the list is emptied. A TwoWay
    /// SelectedIndex binding is itself a CollectionChanged listener, so clearing
    /// the items while the box still holds a selection makes the ComboBox try to
    /// map that selection back to a container it no longer has. WinRT answers
    /// with E_INVALIDARG, which reaches managed code as
    /// <c>ArgumentException("The parameter is incorrect.", "container")</c> and
    /// takes the whole refresh down - the crash reported from the Display page.
    /// Nothing selected, then the new list, then the new selection: every
    /// listener sees a consistent state at every step.
    /// </summary>
    private void SetScalingModes(NvidiaScalingLocation location, NvidiaScalingMode mode)
    {
        _scalingModes = NvidiaScalingMap.ModesFor(location);

        _rebinding = true;
        try
        {
            if (ScalingModeIndex != -1) ScalingModeIndex = -1;

            ScalingModeChoices.Clear();
            foreach (var m in _scalingModes)
            {
                ScalingModeChoices.Add(NvidiaScalingMap.LabelFor(m));
            }
        }
        finally
        {
            _rebinding = false;
        }

        _scalingMode = mode;
        int index = ScalingModeIndexFor(mode);
        if (index >= 0)
        {
            ScalingModeIndex = index;
        }
    }

    /// <summary>
    /// Keeps <paramref name="desired"/> when <paramref name="location"/> can still express
    /// it, otherwise takes the first mode that location does support. Switching from
    /// "No scaling" on the GPU to the monitor should not leave the panel holding a mode the
    /// driver cannot reach.
    /// </summary>
    private static NvidiaScalingMode ReachableModeFor(NvidiaScalingLocation location, NvidiaScalingMode desired)
    {
        var supported = NvidiaScalingMap.ModesFor(location);
        return supported.Contains(desired) ? desired : supported[0];
    }

    // ── status ──
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready.";
    [ObservableProperty]
    public partial string StatusDetails { get; set; } = "";
    [ObservableProperty]
    public partial bool StatusIsError { get; set; }
    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }
    [ObservableProperty]
    public partial bool IsAwaitingConfirmation { get; set; }
    [ObservableProperty]
    public partial string CountdownText { get; set; } = "";

    // Formatted mirrors. Binding a double straight into a TextBlock's Text relies on an
    // implicit conversion that x:Bind does not always emit, so the strings are produced here.
    public string DigitalVibranceText => $"{DigitalVibrance:F0}";

    /// <summary>
    /// Brightness and contrast read "—" only when the driver does not expose them at all.
    /// When they are supported the value is real, and 0.50 is the driver's untouched
    /// setting rather than a dark picture, so the sliders are centred on it.
    /// </summary>
    public string BrightnessText => SupportsBrightness ? $"{Brightness:F2}" : "—";
    public string ContrastText => SupportsContrast ? $"{Contrast:F2}" : "—";
    public string GammaText => $"{Gamma:F2}";
    public string HueText => $"{Hue:F0}°";
    public string RedGainText => $"{RedGain:F2}";
    public string GreenGainText => $"{GreenGain:F2}";
    public string BlueGainText => $"{BlueGain:F2}";

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand RevertCommand { get; }
    public IRelayCommand SaveProfileCommand { get; }
    public IRelayCommand OpenProfileInspectorCommand { get; }
    public IRelayCommand KeepChangesCommand { get; }
    public IAsyncRelayCommand RevertNowCommand { get; }

    public bool HasSelection => SelectedDisplay is not null;
    public bool SupportsDigitalVibrance => SelectedDisplay?.SupportsDigitalVibrance ?? false;
    public bool SupportsBrightness => SelectedDisplay?.SupportsBrightness ?? false;
    public bool SupportsContrast => SelectedDisplay?.SupportsContrast ?? false;
    public bool SupportsGamma => SelectedDisplay?.SupportsGamma ?? false;
    public bool SupportsHue => SelectedDisplay?.SupportsHue ?? false;
    public bool SupportsScaling => SelectedDisplay?.SupportsScaling ?? false;
    public bool SupportsColorData => SelectedDisplay?.SupportsColorData ?? false;

    /// <summary>
    /// Explains the HDR switch rather than leaving the toggle to imply the panel cannot do
    /// HDR at all. Windows owns this control, so a display that supports HDR can still be
    /// blocked from turning it on here — and saying "not supported" in that case was the
    /// panel claiming the hardware cannot do something it can.
    /// </summary>
    public string HdrStatusNote
    {
        get
        {
            var display = SelectedDisplay;
            if (display is null) return "";
            if (!display.SupportsHdr) return "This display does not report HDR support.";
            if (display.HdrBlocked)
                return "Windows has force-disabled HDR on this display, so it cannot be turned " +
                       "on from here. Turn HDR on in Windows display settings first.";
            return display.HdrEnabled ? "HDR is on." : "HDR is off.";
        }
    }
    public bool SupportsColorGain => SelectedDisplay?.SupportsColorGain ?? false;
    public bool SupportsHdr => SelectedDisplay?.SupportsHdr ?? false;
    public bool SupportsAnyControl => SelectedDisplay?.HasAnyControl ?? false;

    /// <summary>True when this session could not map the display to a physical monitor.</summary>
    public bool MonitorControlsUnreachable => SelectedDisplay?.MonitorControlsUnreachable ?? false;

    /// <summary>
    /// Why the channel gains are unavailable, or empty when they work. Brightness and
    /// contrast are driver-side and do not appear here; only the monitor-side controls can
    /// be unreachable, and they need to say so rather than sitting there looking dead.
    /// </summary>
    public string ColorNote
    {
        get
        {
            if (SelectedDisplay is null) return "";
            if (MonitorControlsUnreachable)
                return "Windows would not hand this display a physical monitor, so the monitor's " +
                       "own DDC/CI controls cannot be reached in this session. Brightness, contrast, " +
                       "digital vibrance and hue are driver-side and still work.";
            if (!SupportsColorGain)
                return "This monitor reports no channel-gain control over DDC/CI.";
            return "";
        }
    }

    public NvidiaSettingsViewModel()
    {
        // The revert countdown is driven by a System.Threading.Timer, which fires on a
        // pool thread, and every property it touches is bound to the panel. Capturing the
        // queue here is what lets the tick hop back onto the UI thread; without it the
        // countdown throws the moment the first Apply succeeds.
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => HasPendingChanges && !IsBusy);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => HasPendingChanges && !IsBusy);
        RevertCommand = new AsyncRelayCommand(RevertAsync, () => IsAwaitingConfirmation && !IsBusy);
        SaveProfileCommand = new RelayCommand(SaveProfile);
        OpenProfileInspectorCommand = new RelayCommand(OpenProfileInspector);
        KeepChangesCommand = new RelayCommand(KeepChanges);
        RevertNowCommand = new AsyncRelayCommand(RevertNowAsync, () => IsAwaitingConfirmation);
    }

    /// <summary>Reads the driver state for every attached display.</summary>
    /// <remarks>
    /// Every stage is individually guarded. The vendor path and the Windows path
    /// can each fail on a machine that drives its panel through the iGPU, and a
    /// throw from either used to leave the page as a red banner with no rows:
    /// the exact bug reported as "Could not read the displays. / The parameter
    /// is incorrect. / container". Stages fall forward instead - the worst case
    /// is now an honest "no displays" state, never a dead page.
    /// </remarks>
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Reading display state…";
        StatusDetails = "";
        string? failure = null;
        try
        {
            // -- saved profiles -------------------------------------------------
            try { LoadSavedProfiles(); }
            catch (Exception ex) { failure = Describe(ex); }

            // -- the NVIDIA path ------------------------------------------------
            List<NvidiaDisplayProfile> profiles;
            try
            {
                profiles = (await Task.Run(NvidiaDisplayService.Enumerate)).ToList();
            }
            catch (Exception ex)
            {
                // The service already falls back internally; reaching here means
                // the throw came from outside its guards, so fall back again and
                // name the source (type included) so a bare OS-message exception
                // is at least traceable.
                failure = Describe(ex);
                profiles = SafeOperatingSystemProfiles(ref failure);
            }

            // -- friendly names from Windows' own topology ----------------------
            try
            {
                var osDisplays = DisplayEnumerationService.Enumerate();
                foreach (var p in profiles)
                {
                    var match = osDisplays.FirstOrDefault(d => d.DeviceName.Equals(p.DeviceName, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrWhiteSpace(match.FriendlyName))
                    {
                        p.MonitorName = match.FriendlyName;
                    }
                }
            }
            catch { }

            // -- guarantee rows --------------------------------------------------
            if (profiles.Count == 0)
                profiles = SafeOperatingSystemProfiles(ref failure);

            if (profiles.Count == 0)
            {
                // Selection first, then the list (same container hazard as below).
                SelectedDisplay = null;
                Displays.Clear();
                StatusIsError = true;
                StatusText = "No displays found.";
                StatusDetails = failure
                    ?? (string.IsNullOrWhiteSpace(NvidiaDisplayService.UnavailableReason)
                        ? "Windows reported no display topology."
                        : NvidiaDisplayService.UnavailableReason);
                return;
            }

            // -- bind. The selection is dropped BEFORE the list is emptied - a
            //    TwoWay SelectedItem is a CollectionChanged listener, and
            //    clearing under a live selection makes the ComboBox resolve a
            //    container it no longer owns (WinRT E_INVALIDARG, reported as
            //    ArgumentException "The parameter is incorrect. (container)"). --
            try
            {
                SelectedDisplay = null;
                Displays.Clear();
                foreach (var p in profiles) Displays.Add(p);
            }
            catch (Exception ex) { failure ??= Describe(ex); }

            if (Displays.Count == 0)
            {
                // Binding threw before any row made it in - the honest state.
                SelectedDisplay = null;
                StatusIsError = true;
                StatusText = "No displays found.";
                StatusDetails = failure ?? "The display list could not be bound to the panel.";
                return;
            }

            try
            {
                // Keep the same panel selected across refreshes when it is still attached.
                var keep = SelectedDisplay;
                SelectedDisplay = keep is null
                    ? Displays[0]
                    : Displays.FirstOrDefault(d => d.DisplayId == keep.DisplayId) ?? Displays[0];
            }
            catch (Exception ex)
            {
                failure ??= Describe(ex);
                try { SelectedDisplay = Displays.Count > 0 ? Displays[0] : null; }
                catch { SelectedDisplay = null; }
            }

            StatusIsError = false;
            StatusText = $"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")} detected.";

            // A list read from Windows rather than the NVIDIA driver means every
            // vendor control below is inert; say why, instead of leaving a wall
            // of disabled sliders with no explanation.
            if (Displays.All(d => !d.IsNvidiaControlled) &&
                !string.IsNullOrWhiteSpace(NvidiaDisplayService.UnavailableReason))
            {
                StatusDetails = NvidiaDisplayService.UnavailableReason;
            }
            else if (failure is not null)
            {
                StatusDetails = failure;
            }
            else
            {
                StatusDetails = "";
            }
        }
        catch (Exception ex)
        {
            // Should be unreachable now; keep it honest instead of crashing.
            StatusIsError = false;
            StatusText = Displays.Count > 0
                ? $"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")} detected."
                : "No displays found.";
            StatusDetails = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// The Windows-topology fallback, which itself cannot throw. The reason for
    /// the failure that led here is kept (first wins) so the page can explain
    /// why the vendor controls are off.
    /// </summary>
    private static List<NvidiaDisplayProfile> SafeOperatingSystemProfiles(ref string? failure)
    {
        try
        {
            return NvidiaDisplayService.EnumerateFromOperatingSystem().ToList();
        }
        catch (Exception ex)
        {
            failure ??= Describe(ex);
            return new List<NvidiaDisplayProfile>();
        }
    }

    /// <summary>Type + message + parameter name, and a line in the log file: a bare
    /// "The parameter is incorrect." says nothing about where it came from.</summary>
    private static string Describe(Exception ex)
    {
        string param = ex is ArgumentException a && !string.IsNullOrWhiteSpace(a.ParamName)
            ? $" (parameter '{a.ParamName}')"
            : "";
        string text = $"{ex.GetType().Name}: {ex.Message}{param}";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "display-panel.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch { /* logging must never break the panel */ }
        return text;
    }

    /// <summary>Pushes the draft to the driver, then re-reads to confirm it stuck.</summary>
    public async Task ApplyAsync()
    {
        if (SelectedDisplay is null || IsBusy) return;

        var before = SelectedDisplay.Clone();
        var desired = BuildDraft();

        NvidiaDisplayApplyResult? result = null;
        string? failure = null;

        IsBusy = true;
        StatusText = "Applying…";
        try
        {
            result = await Task.Run(() => NvidiaDisplayService.Apply(desired));
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            // Cleared before the refresh: RefreshAsync bails out while IsBusy, and skipping it
            // would leave the panel showing the draft instead of the state the driver verified.
            IsBusy = false;
        }

        if (failure is not null)
        {
            StatusIsError = true;
            StatusText = "Apply failed.";
            StatusDetails = failure;
        }
        else if (result!.Applied.Count == 0 && result.Failed.Count == 0)
        {
            StatusIsError = false;
            StatusText = "Nothing to apply — the driver already matches these values.";
            StatusDetails = "";
            HasPendingChanges = false;
        }
        else if (result.AllSucceeded)
        {
            StatusIsError = false;
            StatusText = $"Applied: {string.Join(", ", result.Applied)}.";
            StatusDetails = "";
            HasPendingChanges = false;
            // The staged copy has just been committed, so it must stop shadowing live state.
            ConsumeStagedProfile(desired.DeviceName);
            StartRevertWindow(before);
        }
        else
        {
            StatusIsError = true;
            StatusText = "Some changes were not accepted.";
            StatusDetails = string.Join("  ", result.Failed);
        }

        await RefreshAsync();
        RaiseCommandStates();
    }

    /// <summary>Throws away the unapplied draft and re-reads the driver.</summary>
    public async Task UndoAsync()
    {
        if (SelectedDisplay is null) return;
        StatusText = "Discarding unsaved changes…";
        HasPendingChanges = false;
        await RefreshAsync();
    }

    /// <summary>Writes the pre-apply values back to the driver.</summary>
    public async Task RevertAsync()
    {
        if (_anchor is null)
        {
            await UndoAsync();
            return;
        }
        await RevertNowAsync();
    }

    private async Task RevertNowAsync()
    {
        var anchor = _anchor;
        EndRevertWindow();
        if (anchor is null) return;

        IsBusy = true;
        StatusText = "Restoring previous values…";
        try
        {
            var result = await Task.Run(() => NvidiaDisplayService.Restore(anchor));
            StatusIsError = !result.AllSucceeded;
            StatusText = result.AllSucceeded ? "Restored the previous values." : "Some values could not be restored.";
            StatusDetails = string.Join("  ", result.Failed);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusText = "Restore failed.";
            StatusDetails = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private void KeepChanges()
    {
        _anchor = null;
        EndRevertWindow();
        StatusText = "Changes kept.";
    }

    // ── draft plumbing ──

    partial void OnSelectedDisplayChanged(NvidiaDisplayProfile? value) => LoadDraft(value);

    private void LoadDraft(NvidiaDisplayProfile? profile)
    {
        _syncing = true;
        try
        {
            if (profile is null)
            {
                DigitalVibrance = Brightness = Contrast = Hue = 0;
                Gamma = 1.0;
                HasPendingChanges = false;
                return;
            }

            // A previously saved profile is offered as a draft rather than applied behind
            // the user's back, so nothing changes on the desktop without them asking.
            var saved = _savedProfiles.TryGetValue(profile.DeviceName, out var s) ? s : null;
            var source = saved ?? profile;

            DigitalVibrance = source.DigitalVibrance;
            Brightness = source.Brightness;
            Contrast = source.Contrast;
            Gamma = source.Gamma <= 0 ? 1.0 : source.Gamma;
            Hue = source.Hue;
            HdrEnabled = source.HdrEnabled;

            var gain = source.ColorGain;
            RedGain = gain.Red > 0 ? gain.Red : 1.0;
            GreenGain = gain.Green > 0 ? gain.Green : 1.0;
            BlueGain = gain.Blue > 0 ? gain.Blue : 1.0;

            _scalingLocation = source.ScalingLocation;
            _scalingMode = source.ScalingMode;
            _colorDepth = source.ColorDepth;
            _colorFormat = source.ColorFormat;
            _dynamicRange = source.DynamicRange;

            // The location index only raises its handler when the value actually moves, so
            // rebuild the mode list explicitly rather than trusting it to already be in step
            // with the location this profile reports.
            ScalingLocationIndex = (int)source.ScalingLocation;
            SetScalingModes(source.ScalingLocation, ReachableModeFor(source.ScalingLocation, source.ScalingMode));
            ColorDepthIndex = (int)source.ColorDepth;
            ColorFormatIndex = (int)source.ColorFormat;
            // The reachable ranges depend on the format just set above, so rebuild the list
            // rather than assuming it already matches.
            PopulateDynamicRangeChoices(BuildDynamicRangeChoices(source.ColorFormat));
            _dynamicRange = NvidiaColorMap.ReachableRangeFor(source.ColorFormat, source.DynamicRange);
            ReassertDynamicRangeSelection();
            HasPendingChanges = saved is not null && saved.Diff(profile).Count > 0;
        }
        finally
        {
            _syncing = false;
        }
        RaiseCommandStates();
    }

    private NvidiaDisplayProfile BuildDraft()
    {
        var profile = SelectedDisplay!.Clone();
        profile.DigitalVibrance = DigitalVibrance;
        profile.Brightness = Brightness;
        profile.Contrast = Contrast;
        profile.Gamma = Gamma;
        profile.Hue = Hue;
        profile.ScalingLocation = _scalingLocation;
        profile.ScalingMode = _scalingMode;
        profile.ColorGain = (RedGain, GreenGain, BlueGain);

        // Colour depth, colour format, dynamic range and HDR deliberately keep the values
        // the driver reports: the controls that used to change them are gone, so the only
        // way a differing value could reach Apply is a stored profile being loaded back —
        // and that would push a setting to the display with nothing on screen to show it.

        // A control the monitor would not report starts out holding a placeholder. Moving
        // it away from that placeholder is the user choosing a value, so from here on it
        // is a real reading and the diff is allowed to see it.
        if (profile.ColorGainIsUnread &&
            (Math.Abs(RedGain - 1.0) > 0.001 || Math.Abs(GreenGain - 1.0) > 0.001 ||
             Math.Abs(BlueGain - 1.0) > 0.001))
            profile.ColorGainIsUnread = false;

        return profile;
    }

    partial void OnHdrEnabledChanged(bool value) => MarkDirty();

    // The InfoBar takes an enum, the rest of the panel reasons about a bool. Deriving one
    // from the other here keeps every status site from having to remember both.
    partial void OnStatusIsErrorChanged(bool value)
        => StatusSeverity = value ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    partial void OnDigitalVibranceChanged(double value) { OnPropertyChanged(nameof(DigitalVibranceText)); MarkDirty(); }
    partial void OnBrightnessChanged(double value) { OnPropertyChanged(nameof(BrightnessText)); MarkDirty(); }
    partial void OnContrastChanged(double value) { OnPropertyChanged(nameof(ContrastText)); MarkDirty(); }
    partial void OnGammaChanged(double value) { OnPropertyChanged(nameof(GammaText)); MarkDirty(); }
    partial void OnHueChanged(double value) { OnPropertyChanged(nameof(HueText)); MarkDirty(); }
    partial void OnRedGainChanged(double value) { OnPropertyChanged(nameof(RedGainText)); MarkDirty(); }
    partial void OnGreenGainChanged(double value) { OnPropertyChanged(nameof(GreenGainText)); MarkDirty(); }
    partial void OnBlueGainChanged(double value) { OnPropertyChanged(nameof(BlueGainText)); MarkDirty(); }

    partial void OnScalingLocationIndexChanged(int value)
    {
        _scalingLocation = ClampEnum<NvidiaScalingLocation>(value);
        RetargetScalingModes(_scalingLocation, _scalingMode);
        MarkDirty();
    }

    partial void OnScalingModeIndexChanged(int value)
    {
        // A push-back that lands while a list is being rebuilt is stale by
        // definition: the ComboBox sends it because the items it was showing
        // just changed underneath it. Acting on it would re-enter the rebuild
        // (and re-arm the stale-selection crash below), so it is ignored. The
        // rebuild sets the correct index itself, once the list is whole.
        if (_rebinding) return;

        if (value < 0 || value >= _scalingModes.Count)
        {
            // SelectedIndex is TwoWay, so the ComboBox pushes -1 back into this property
            // whenever it swaps its item list, and that push lands after the code that set
            // the correct index. Returning here is what left the box empty: the property
            // stayed at -1 and nothing ever put the real value back. _scalingMode is
            // untouched by this, so the correct entry is still known.
            int correct = ScalingModeIndexFor(_scalingMode);
            if (correct >= 0 && correct != value)
            {
                ScalingModeIndex = correct;
            }
            return;
        }

        _scalingMode = _scalingModes[value];
        MarkDirty();
    }

    /// <summary>
    /// Rebuilds the mode list for a new location and re-points the selection, keeping the
    /// current mode when the new location can still do it and otherwise falling back to one
    /// it can. Switching from "No scaling" on the GPU to the monitor should not leave the
    /// panel showing a mode the driver cannot reach.
    /// </summary>
    private void RetargetScalingModes(NvidiaScalingLocation location, NvidiaScalingMode desired)
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            SetScalingModes(location, ReachableModeFor(location, desired));
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    partial void OnColorDepthIndexChanged(int value)
    {
        _colorDepth = ClampEnum<NvidiaColorDepthOption>(value);
        MarkDirty();
    }

    partial void OnColorFormatIndexChanged(int value)
    {
        _colorFormat = ClampEnum<NvidiaColorFormatOption>(value);
        RetargetDynamicRanges(_colorFormat, _dynamicRange);
        MarkDirty();
    }

    /// <summary>
    /// Rebuilds the dynamic-range list for a new colour format and re-points the selection,
    /// keeping the current range when the format still allows it. YCbCr is limited-range
    /// only, so switching to one has to move the range rather than leave an unreachable
    /// value sitting in the panel.
    /// </summary>
    private void RetargetDynamicRanges(NvidiaColorFormatOption format, NvidiaDynamicRangeOption desired)
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            PopulateDynamicRangeChoices(BuildDynamicRangeChoices(format));

            _dynamicRange = NvidiaColorMap.ReachableRangeFor(format, desired);
            ReassertDynamicRangeSelection();
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    partial void OnDynamicRangeChoiceChanged(DynamicRangeChoice? value)
    {
        // Stale push-back from a list rebuild - dropped, same as the scaling
        // mode index. Reasserting now would target a half-built list.
        if (_rebinding) return;

        if (value is null)
        {
            // Spurious reset from a list rebuild, same as the scaling mode dropdown.
            ReassertDynamicRangeSelection();
            return;
        }

        _dynamicRange = value.Option;
        MarkDirty();
    }

    private void ReassertDynamicRangeSelection()
    {
        var wanted = RangeChoiceFor(_dynamicRange);
        if (wanted is null) return;

        if (!ReferenceEquals(DynamicRangeChoice, wanted))
            DynamicRangeChoice = wanted;

        if (_dispatcherQueue is null) return;
        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            var still = RangeChoiceFor(_dynamicRange);
            if (still is not null && !ReferenceEquals(DynamicRangeChoice, still))
                DynamicRangeChoice = still;
        });
    }

    private static TEnum ClampEnum<TEnum>(int value) where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        return value >= 0 && value < values.Length ? values[value] : values[0];
    }

    private void MarkDirty()
    {
        if (_syncing || SelectedDisplay is null) return;
        if (SelectedDisplay.Diff(BuildDraft()).Count > 0)
        {
            HasPendingChanges = true;
        }
        else
        {
            HasPendingChanges = false;
        }
        RaiseCommandStates();
    }

    partial void OnHasPendingChangesChanged(bool value) => RaiseCommandStates();

    // ── revert window ──

    private void StartRevertWindow(NvidiaDisplayProfile previous)
    {
        _anchor = previous;
        _secondsRemaining = RevertWindowSeconds;
        IsAwaitingConfirmation = true;
        UpdateCountdownText();

        _revertTimer?.Dispose();
        _revertTimer = new System.Threading.Timer(_ => OnRevertTick(), null, 1000, 1000);
        RaiseCommandStates();
    }

    /// <summary>
    /// One tick of the revert countdown.
    ///
    /// The timer raises this on a pool thread, and the whole body is UI-bound — the
    /// countdown text, the banner's visibility, and the restore itself — so it is handed to
    /// the dispatcher rather than run where it was called from. Doing the work here instead
    /// of at each assignment is what keeps the restore, which resumes after an await, on
    /// the UI thread too.
    /// </summary>
    private void OnRevertTick()
    {
        if (_dispatcherQueue is null) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            // A tick that was already in flight when the window was ended must not bring
            // the countdown back.
            if (_revertTimer is null) return;

            if (_secondsRemaining <= 1)
            {
                EndRevertWindow();
                _ = RevertNowAsync();
                return;
            }

            _secondsRemaining--;
            UpdateCountdownText();
        });
    }

    private void UpdateCountdownText()
        => CountdownText = $"Reverting in {_secondsRemaining}s unless you keep these values.";

    private void EndRevertWindow()
    {
        _revertTimer?.Dispose();
        _revertTimer = null;
        IsAwaitingConfirmation = false;
        CountdownText = "";
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RevertNowCommand.NotifyCanExecuteChanged();
    }

    // ── saved profiles ──
    private void SaveProfile()
    {
        if (SelectedDisplay is null) return;
        try
        {
            _savedProfiles[SelectedDisplay.DeviceName] = BuildDraft();
            var dir = Path.GetDirectoryName(ProfileStorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ProfileStorePath,
                JsonSerializer.Serialize(_savedProfiles, new JsonSerializerOptions { WriteIndented = true }));
            HasPendingChanges = false;
            StatusIsError = false;
            StatusText = $"Saved a profile for {SelectedDisplay.DeviceName}.";
            StatusDetails = "It will be offered as a draft the next time this panel opens.";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusText = "Could not save the profile.";
            StatusDetails = ex.Message;
        }
    }

    /// <summary>
    /// Hands off to the bundled Profile Inspector. The two driver-profile flags on this page
    /// have no documented NVAPI surface, so this is the supported way to change them.
    /// </summary>
    private void OpenProfileInspector()
    {
        try
        {
            string exePath = Path.Combine(AppContext.BaseDirectory, "Assets", "nvidiaProfileInspector",
                                           "nvidiaProfileInspector.exe");
            if (!File.Exists(exePath))
            {
                StatusIsError = true;
                StatusText = "Profile Inspector is not bundled with this build.";
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusText = "Could not launch Profile Inspector.";
            StatusDetails = ex.Message;
        }
    }

    /// <summary>Drops a staged profile once it has been committed to the driver.</summary>
    private void ConsumeStagedProfile(string deviceName)
    {
        if (!_savedProfiles.Remove(deviceName)) return;
        try
        {
            var dir = Path.GetDirectoryName(ProfileStorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (_savedProfiles.Count == 0)
            {
                if (File.Exists(ProfileStorePath)) File.Delete(ProfileStorePath);
                return;
            }
            File.WriteAllText(ProfileStorePath,
                JsonSerializer.Serialize(_savedProfiles, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing the staged copy is harmless; the live values are already applied.
        }
    }

    private void LoadSavedProfiles()
    {
        _savedProfiles.Clear();
        try
        {
            if (!File.Exists(ProfileStorePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, NvidiaDisplayProfile>>(File.ReadAllText(ProfileStorePath));
            if (loaded is null) return;
            foreach (var (key, value) in loaded) _savedProfiles[key] = value;
        }
        catch
        {
            // A corrupt profile file must never stop the panel from working.
        }
    }
}

/// <summary>
/// One entry in the "Dynamic range" list. Shared instances so that rebuilding the list does
/// not make a ComboBox drop a still-valid selection.
/// </summary>
public sealed class DynamicRangeChoice
{
    public static readonly DynamicRangeChoice Full = new(NvidiaDynamicRangeOption.Full);
    public static readonly DynamicRangeChoice Limited = new(NvidiaDynamicRangeOption.Limited);

    private DynamicRangeChoice(NvidiaDynamicRangeOption option) => Option = option;

    public NvidiaDynamicRangeOption Option { get; }

    public string Label => Option == NvidiaDynamicRangeOption.Full ? "Full (0-255)" : "Limited (16-235)";

    public override string ToString() => Label;

    public static DynamicRangeChoice For(NvidiaDynamicRangeOption option) =>
        option == NvidiaDynamicRangeOption.Full ? Full : Limited;
}
