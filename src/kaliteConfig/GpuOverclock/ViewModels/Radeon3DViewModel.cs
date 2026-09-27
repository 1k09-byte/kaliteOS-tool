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
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.GpuOverclock.ViewModels
{
    /// <summary>One AMD adapter in the picker, plus its live ADLX session.</summary>
    public sealed partial class Radeon3DAdapterItem : ObservableObject
    {
        internal Radeon3DAdapterItem(AmdRadeon3DSession session)
        {
            Session = session;
            Name = string.IsNullOrWhiteSpace(session.Identity.Name)
                ? "AMD display adapter"
                : session.Identity.Name;
            PnpString = session.Identity.PnpString;
            IsDiscrete = session.Identity.IsDiscrete;
        }

        internal AmdRadeon3DSession Session { get; }

        public string Name { get; }
        public string PnpString { get; }
        public bool IsDiscrete { get; }

        /// <summary>What the ComboBox shows: the name, and whether it is a dGPU or an iGPU.</summary>
        public string DisplayLabel => $"{Name}  ({(IsDiscrete ? "discrete" : "integrated")})";
    }

    /// <summary>
    /// One feature row, as the page shows it. Every state here is a read-back
    /// from the driver: <see cref="IsEnabled"/> is what the driver last reported,
    /// never what the user clicked, so a write the driver quietly refused shows
    /// up as a toggle that snapped back.
    /// </summary>
    public sealed partial class Radeon3DSettingItem : ObservableObject
    {
        internal Radeon3DSettingItem(Radeon3DFeatureInfo info)
        {
            Info = info;
            RangeMinimum = 0;
            RangeMaximum = 0;
            RangeStep = 0;
        }

        internal Radeon3DFeatureInfo Info { get; }

        public string Title => Info.Title;
        public string Glyph => Info.Glyph;
        public string Summary => Info.Summary;
        public string ParameterLabel => Info.ParameterLabel;
        public Radeon3DSetting Setting => Info.Setting;

        /// <summary>True when this row is a property of the display, not the adapter.</summary>
        public bool IsDisplayLevel => Info.Scope == Radeon3DScope.DisplayLevel;

        [ObservableProperty] private bool _isSupported;
        [ObservableProperty] private bool _isBusy;

        /// <summary>
        /// What the driver last reported, NOT what the user clicked.
        ///
        /// Hand-written rather than [ObservableProperty] because it must raise a
        /// change notification on EVERY assignment, including one that sets the
        /// same value again. The toggle is bound one-way to this, and a one-way
        /// binding only re-reads its source when the source says it changed - so
        /// if the driver quietly refused a write, an unchanged-value notification
        /// would never fire and the switch would stay showing "on" for a feature
        /// that is off. Re-asserting the same value costs nothing and makes the
        /// refusal visible.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;

                // Unconditional, and with CallerMemberName so it reports
                // "IsEnabled" for the binding either way.
                OnPropertyChanged();
                RaiseRowFlags();
            }
        }

        private bool _isEnabled;

        /// <summary>"Supported" / "Not supported on this adapter" / "no ADLX".</summary>
        [ObservableProperty] private string _supportText = string.Empty;

        /// <summary>Set only when the feature is unavailable, so the row can say why.</summary>
        [ObservableProperty] private string? _unsupportedReason;

        // ---- the driver's own value range, never a hardcoded one ----------------

        [ObservableProperty] private double _rangeMinimum;
        [ObservableProperty] private double _rangeMaximum;
        [ObservableProperty] private double _rangeStep;
        [ObservableProperty] private bool _hasRange;

        // Chill's floor and ceiling.
        [ObservableProperty] private double _minFps;
        [ObservableProperty] private double _maxFps;

        // Every other numeric parameter shares one control.
        [ObservableProperty] private double _singleValue;

        // ---- what the row shows -------------------------------------------------

        /// <summary>The toggle is only live when the driver supports the feature.</summary>
        public bool IsRowEnabled => IsSupported && !IsBusy;

        /// <summary>The parameter controls, when the row has any at all.</summary>
        public bool HasParameters => Info.HasParameter && HasRange;

        /// <summary>Chill is the only feature with a pair of values.</summary>
        public bool HasFpsPair => HasParameters
            && (Info.Parameter == Radeon3DParameter.MinFps || Info.Parameter == Radeon3DParameter.MaxFps);

        public bool HasSingleValue => HasParameters && !HasFpsPair;

        /// <summary>
        /// Parameters are only editable while the feature is actually on. Adrenalin
        /// does the same: a Chill ceiling is meaningless while Chill is off, and
        /// some drivers reject the write outright.
        /// </summary>
        public bool AreParametersEnabled => IsRowEnabled && IsEnabled;

        partial void OnIsSupportedChanged(bool value) => RaiseRowFlags();
        partial void OnIsBusyChanged(bool value) => RaiseRowFlags();
        partial void OnHasRangeChanged(bool value) => RaiseRowFlags();

        private void RaiseRowFlags()
        {
            OnPropertyChanged(nameof(IsRowEnabled));
            OnPropertyChanged(nameof(HasParameters));
            OnPropertyChanged(nameof(HasFpsPair));
            OnPropertyChanged(nameof(HasSingleValue));
            OnPropertyChanged(nameof(AreParametersEnabled));
        }

        /// <summary>Pushes a fresh driver read into this row.</summary>
        internal void ApplyRead(bool? supported, bool? enabled, AdlxIntRange? range, int? minFps, int? maxFps, int? single)
        {
            // true = supported, false = the driver said no, null = the interface
            // could not be reached. Both non-true cases hide the toggle, but only
            // one of them is the driver saying "not on this card".
            IsSupported = supported == true;
            UnsupportedReason = supported switch
            {
                false => "The installed AMD driver reports this feature as not supported on this adapter.",
                null => "This AMD driver does not implement this setting, so it cannot be read or changed here.",
                _ => null,
            };
            SupportText = IsSupported
                ? (IsDisplayLevel ? "Supported by this driver (display-level)" : "Supported by this driver")
                : UnsupportedReason ?? "Not available";

            IsEnabled = enabled == true;

            if (range is AdlxIntRange r && RadeonRange.IsUsable(r.MinValue, r.MaxValue, r.Step))
            {
                RangeMinimum = r.MinValue;
                RangeMaximum = r.MaxValue;
                RangeStep = r.Step;
                HasRange = true;
            }
            else
            {
                // No usable range: the row shows no parameter control at all
                // rather than inventing bounds the driver never gave us.
                HasRange = false;
            }

            if (minFps is int m) MinFps = m;
            if (maxFps is int x) MaxFps = x;
            if (single is int v) SingleValue = v;
        }
    }

    /// <summary>
    /// Backing model for the Radeon tab.
    ///
    /// Three rules shape everything here:
    ///
    ///   1. Read before show. A toggle's position is a driver read-back, so a
    ///      write the driver refused visibly snaps back instead of lying.
    ///
    ///   2. Read back ALL of a group after ANY write in it. Chill, Boost and
    ///      Anti-Lag interfere, so knowing only the outcome of the call that was
    ///      made is not enough - the driver is the one that decides which of them
    ///      ends up on, and the page has to show that.
    ///
    ///   3. Say when a setting will not matter. On a machine with an AMD iGPU
    ///      and an NVIDIA dGPU, most games render on the NVIDIA card where none
    ///      of this applies, so the tab states which adapter is actually in play.
    ///
    /// The ADLX side is shared with the Overclock tab: this goes through
    /// AmdAdlxInterop's single system instance, never its own.
    /// </summary>
    public sealed partial class Radeon3DViewModel : ObservableObject
    {
        private readonly List<AmdRadeon3DSession> _sessions = new();

        /// <summary>
        /// The mutually exclusive group, with the restore set this page is
        /// holding for Chill. <see cref="Radeon3DExclusiveGroup"/> is rebuilt
        /// from a read-back after every write, but the restore flags have to
        /// survive that: the driver can tell us what is on, not what we turned
        /// off on its behalf.
        /// </summary>
        private Radeon3DExclusiveGroup _exclusive = Radeon3DExclusiveGroup.None;

        private bool _loaded;

        public Radeon3DViewModel()
        {
            foreach (Radeon3DFeatureInfo info in Radeon3DCatalog.All)
                Items.Add(new Radeon3DSettingItem(info));
        }

        public ObservableCollection<Radeon3DAdapterItem> Adapters { get; } = new();
        public ObservableCollection<Radeon3DSettingItem> Items { get; } = new();

        /// <summary>Chill, Boost and Anti-Lag, and why they cannot be combined.</summary>
        public string ExclusionNote => Radeon3DPolicy.ExclusionExplanation;

        [ObservableProperty] private Radeon3DAdapterItem? _selectedAdapter;
        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _adlxAvailable;
        [ObservableProperty] private string _statusText = "Detecting AMD adapters…";
        [ObservableProperty] private string _driverVersionText = string.Empty;
        [ObservableProperty] private string _vendorSummary = string.Empty;
        [ObservableProperty] private string? _errorMessage;
        [ObservableProperty] private string? _infoNote;
        [ObservableProperty] private string? _writeNote;

        /// <summary>True once the driver's 3D-settings change event is hooked up.</summary>
        [ObservableProperty] private bool _watchingDriverChanges;

        /// <summary>
        /// Null when the driver has no 3D settings change event, which is a
        /// normal answer on an older driver - hence a sentence rather than a
        /// checkbox the user has to interpret.
        /// </summary>
        public string? WatchNote => WatchingDriverChanges
            ? "Listening for 3D setting changes made outside this app, such as in AMD's own software."
            : null;

        /// <summary>The adapter picker is noise on a machine with one AMD GPU.</summary>
        public bool HasMultipleAdapters => Adapters.Count > 1;

        // ---- active renderer ----------------------------------------------------

        [ObservableProperty] private string _rendererApp = string.Empty;
        [ObservableProperty] private string _rendererTitle = string.Empty;
        [ObservableProperty] private string _rendererDetail = string.Empty;
        [ObservableProperty] private bool _rendererApplies;
        [ObservableProperty] private string? _mixedVendorWarning;

        /// <summary>
        /// Raised from the driver's own thread when something outside this app
        /// moved a 3D setting (AMD's Adrenalin software, most likely). The page
        /// marshals it to the UI thread - see the comment on the event.
        /// </summary>
        public event Action? ExternalRefreshRequested;

        /// <summary>Called by the ADLX listener trampoline. Never blocks.</summary>
        internal void OnDriverChanged() => ExternalRefreshRequested?.Invoke();

        // ---- loading --------------------------------------------------------------

        /// <summary>Runs the ADLX probe once per page instance.</summary>
        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        }

        [RelayCommand]
        private Task RefreshAsync() => LoadAsync();

        /// <summary>
        /// Re-reads every row from the driver without re-enumerating adapters.
        /// This is what runs after a write, and what the change listener calls.
        /// </summary>
        [RelayCommand]
        public Task RefreshStateAsync() => ReadAllAsync();

        /// <summary>
        /// Reads the vendor layout and this app's own per-app graphics preference.
        ///
        /// Called from Loaded rather than from the constructor on purpose: it
        /// reads a registry key, and if that key is unreadable the service falls
        /// back to WMI, which takes hundreds of milliseconds. Doing that inside
        /// InitializeComponent would stall the tab before it had drawn anything.
        /// </summary>
        public void InitialiseRendererCard()
        {
            RendererApp = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            CheckRenderer();
        }

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                ErrorMessage = null;

                if (!AmdAdlxInterop.IsAvailable)
                {
                    AdlxAvailable = false;
                    StatusText = "No AMD device library";
                    ErrorMessage =
                        "The AMD display driver did not provide ADLX (amdadlx64.dll), so no Radeon 3D " +
                        "settings can be read or changed. Install or repair the AMD display driver from " +
                        "the Drivers tab, then refresh.";
                    MarkAllUnsupported("ADLX is not available on this machine.");
                    return;
                }

                AdlxAvailable = true;
                DriverVersionText = AmdAdlxInterop.DriverVersion ?? "unknown";

                foreach (AmdRadeon3DSession session in _sessions) session.Dispose();
                _sessions.Clear();

                // The same single ADLX system the Overclock tab uses, through the
                // same GetGPUs list, so this is the same adapter objects.
                foreach (AmdRadeon3DSession session in AmdAdlxInterop.Enumerate3DSettingsSessions())
                    _sessions.Add(session);

                var previous = SelectedAdapter;
                Adapters.Clear();
                foreach (AmdRadeon3DSession session in _sessions)
                    Adapters.Add(new Radeon3DAdapterItem(session));

                if (Adapters.Count == 0)
                {
                    SelectedAdapter = null;
                    StatusText = "No AMD adapters reported";
                    InfoNote =
                        "ADLX is present but the installed AMD driver reported no adapters. If this " +
                        "machine has an AMD card, install its display driver from the Drivers tab.";
                    MarkAllUnsupported("No AMD adapter is available.");
                    return;
                }

                // Keep the user's choice across a refresh where possible.
                SelectedAdapter = Adapters.FirstOrDefault(a =>
                               previous is not null && a.PnpString == previous.PnpString)
                           ?? Adapters[0];

                OnPropertyChanged(nameof(HasMultipleAdapters));
                await ReadAllAsync();

                StatusText = Adapters.Count == 1
                    ? "1 AMD adapter"
                    : $"{Adapters.Count} AMD adapters";
            }
            catch (Exception ex)
            {
                ErrorMessage = "AMD 3D settings could not be read: " + ex.Message;
                StatusText = "Read failed";
            }
            finally
            {
                IsLoading = false;
                CheckRenderer();
            }
        }

        private void MarkAllUnsupported(string reason)
        {
            foreach (Radeon3DSettingItem item in Items)
            {
                item.IsSupported = false;
                item.IsEnabled = false;
                item.HasRange = false;
                item.UnsupportedReason = reason;
                item.SupportText = reason;
            }
        }

        // ---- reading ---------------------------------------------------------------

        private Task ReadAllAsync()
        {
            AmdRadeon3DSession? session = SelectedAdapter?.Session;
            if (session is null) return Task.CompletedTask;

            try
            {
                foreach (Radeon3DSettingItem item in Items)
                {
                    bool? supported = session.ReadSupported(item.Setting);
                    bool? enabled = session.ReadEnabled(item.Setting);

                    AdlxIntRange? range = supported == true
                        ? session.ReadRange(item.Setting, item.Info.Parameter)
                        : null;

                    int? minFps = null, maxFps = null, single = null;
                    if (supported == true)
                    {
                        if (item.Info.Parameter == Radeon3DParameter.MinFps)
                        {
                            minFps = session.ReadValue(item.Setting, Radeon3DParameter.MinFps);
                            maxFps = session.ReadValue(item.Setting, Radeon3DParameter.MaxFps);
                        }
                        else if (item.Info.HasParameter)
                        {
                            single = session.ReadValue(item.Setting, item.Info.Parameter);
                        }
                    }

                    item.ApplyRead(supported, enabled, range, minFps, maxFps, single);
                }

                MergeExclusiveReadback(ReadExclusiveGroup(session), _exclusive);
                WatchingDriverChanges = session.TryStartListening();
            }
            catch (Exception ex)
            {
                ErrorMessage = "Read failed: " + ex.Message;
            }

            return Task.CompletedTask;
        }

        private static Radeon3DExclusiveGroup ReadExclusiveGroup(AmdRadeon3DSession session)
            => Radeon3DExclusiveGroup.FromReadback(
                session.ReadEnabled(Radeon3DSetting.AntiLag) == true,
                session.ReadEnabled(Radeon3DSetting.Boost) == true,
                session.ReadEnabled(Radeon3DSetting.Chill) == true);

        /// <summary>
        /// Folds a fresh read-back into the tracked group. The booleans always
        /// come from the driver; only the restore flags come from us, and they
        /// only mean anything while Chill is actually on.
        /// </summary>
        private void MergeExclusiveReadback(Radeon3DExclusiveGroup readBack, Radeon3DExclusiveGroup planState)
        {
            if (!readBack.ChillEnabled)
            {
                // Without Chill there is nothing to restore: the set is void.
                _exclusive = readBack;
                return;
            }

            Radeon3DExclusiveGroup merged = readBack with
            {
                RestoreBoost = planState.RestoreBoost && !readBack.BoostEnabled,
                RestoreAntiLag = planState.RestoreAntiLag && !readBack.AntiLagEnabled,
            };

            // The driver refusing a disable is worth saying out loud: the two are
            // on at once, which is the state the whole policy exists to prevent.
            // Appended, not assigned - ApplyToggleAsync writes its own summary
            // straight after this, and the conflict is the more important half.
            if ((merged.ChillEnabled && merged.BoostEnabled) || (merged.ChillEnabled && merged.AntiLagEnabled))
            {
                const string conflict =
                    "The driver left a mutually exclusive feature switched on alongside Chill. " +
                    "Close any running game and try again.";
                WriteNote = string.IsNullOrEmpty(WriteNote) ? conflict : WriteNote + "  " + conflict;
            }

            _exclusive = merged;
        }

        // ---- writing ---------------------------------------------------------------

        /// <summary>
        /// Applies a toggle. Issues the writes the policy asks for, in order,
        /// then re-reads every row so the page shows what the driver actually
        /// did rather than what was requested.
        /// </summary>
        public async Task ApplyToggleAsync(Radeon3DSettingItem item, bool requested)
        {
            AmdRadeon3DSession? session = SelectedAdapter?.Session;
            if (session is null || item is null || item.IsBusy) return;

            if (!item.IsSupported)
            {
                WriteNote = item.UnsupportedReason ?? "That feature is not available on this adapter.";
                await ReadAllAsync();
                return;
            }

            var notes = new List<string>();
            item.IsBusy = true;
            try
            {
                bool exclusive = Radeon3DPolicy.ConflictsWith(item.Setting).Count > 0;
                if (exclusive)
                {
                    Radeon3DTransition plan = Radeon3DPolicy.Request(_exclusive, item.Setting, requested);
                    foreach (Radeon3DWrite write in plan.Writes)
                    {
                        int result = session.WriteEnabled(write.Setting, write.Enable);
                        notes.Add(RadeonResult.Explain(Describe(write.Setting), result)
                                  + " " + write.Reason);
                    }

                    // The plan knows the restore set; the read-back below decides
                    // which of those writes actually took.
                    _exclusive = plan.After;
                }
                else
                {
                    int result = session.WriteEnabled(item.Setting, requested);
                    notes.Add(RadeonResult.Explain(item.Title, result));
                    _exclusive = item.Setting switch
                    {
                        Radeon3DSetting.AntiLag => _exclusive.With(Radeon3DSetting.AntiLag, requested),
                        Radeon3DSetting.Boost => _exclusive.With(Radeon3DSetting.Boost, requested),
                        _ => _exclusive,
                    };
                }
            }
            catch (Exception ex)
            {
                notes.Add("The write could not be issued: " + ex.Message);
            }
            finally
            {
                item.IsBusy = false;
            }

            // Rule 2: re-read everything the group touches, not just the row
            // that was written.
            await ReadAllAsync();

            // Only ever ADD to a note. A write with nothing to do - which happens
            // when re-asserting a value the driver already had - must not wipe
            // the message from the write that actually happened.
            if (notes.Count > 0)
            {
                string summary = string.Join("  ", notes);
                WriteNote = string.IsNullOrEmpty(WriteNote) ? summary : WriteNote + "  " + summary;
            }
        }

        /// <summary>
        /// Applies a feature's numeric parameter, validated against the range
        /// the driver reported for it.
        /// </summary>
        public async Task ApplyParameterAsync(Radeon3DSettingItem item)
        {
            AmdRadeon3DSession? session = SelectedAdapter?.Session;
            if (session is null || item is null || item.IsBusy) return;

            if (!item.IsSupported || !item.HasRange)
            {
                WriteNote = item.UnsupportedReason ?? "This setting has no usable range on this adapter.";
                await ReadAllAsync();
                return;
            }

            if (!item.IsEnabled)
            {
                WriteNote = $"{item.Title} is off. Turn it on before setting a value.";
                await ReadAllAsync();
                return;
            }

            int rangeMin = (int)item.RangeMinimum;
            int rangeMax = (int)item.RangeMaximum;
            int rangeStep = (int)item.RangeStep;

            var notes = new List<string>();
            item.IsBusy = true;
            try
            {
                if (item.HasFpsPair)
                {
                    if (!RadeonRange.TryNormalizeFpsPair(
                            (int)item.MinFps, (int)item.MaxFps,
                            rangeMin, rangeMax, rangeStep,
                            out int normalizedMin, out int normalizedMax, out string? note))
                    {
                        WriteNote = note;
                        return;
                    }

                    if (note is not null) notes.Add(note);
                    if (normalizedMin != (int)item.MinFps || normalizedMax != (int)item.MaxFps)
                    {
                        item.MinFps = normalizedMin;
                        item.MaxFps = normalizedMax;
                    }

                    int minResult = session.WriteValue(
                        item.Setting, Radeon3DParameter.MinFps, normalizedMin);
                    int maxResult = session.WriteValue(
                        item.Setting, Radeon3DParameter.MaxFps, normalizedMax);
                    notes.Add(RadeonResult.Explain($"{item.Title} minimum", minResult));
                    notes.Add(RadeonResult.Explain($"{item.Title} maximum", maxResult));
                }
                else
                {
                    int value = RadeonRange.Clamp((int)item.SingleValue, rangeMin, rangeMax, rangeStep);
                    if (value != (int)item.SingleValue) item.SingleValue = value;
                    int result = session.WriteValue(item.Setting, item.Info.Parameter, value);
                    notes.Add(RadeonResult.Explain($"{item.Title} {item.ParameterLabel.ToLowerInvariant()}", result));
                }
            }
            catch (Exception ex)
            {
                notes.Add("The write could not be issued: " + ex.Message);
            }
            finally
            {
                item.IsBusy = false;
            }

            await ReadAllAsync();
            if (notes.Count > 0)
            {
                string summary = string.Join("  ", notes);
                WriteNote = string.IsNullOrEmpty(WriteNote) ? summary : WriteNote + "  " + summary;
            }
        }

        private static string Describe(Radeon3DSetting setting)
        {
            try { return Radeon3DCatalog.Info(setting).Title; }
            catch (ArgumentOutOfRangeException) { return setting.ToString(); }
        }

        partial void OnWatchingDriverChangesChanged(bool value)
            => OnPropertyChanged(nameof(WatchNote));

        partial void OnSelectedAdapterChanged(Radeon3DAdapterItem? value)
        {
            if (value is null) return;
            _exclusive = Radeon3DExclusiveGroup.None;
            _ = ReadAllAsync();
        }

        // ---- active renderer --------------------------------------------------------

        [RelayCommand]
        private void CheckRenderer()
        {
            var vendors = GpuVendorPresenceService.Detect();
            VendorSummary = vendors.Summary;
            MixedVendorWarning = RendererPolicy.MixedVendorWarning(vendors.HasNvidia, vendors.HasAmd);

            RendererVerdict verdict = GpuVendorPresenceService.EvaluateRenderer(vendors, RendererApp);
            RendererTitle = verdict.Title;
            RendererDetail = verdict.Detail;
            RendererApplies = verdict.SettingsApply;
        }
    }
}
