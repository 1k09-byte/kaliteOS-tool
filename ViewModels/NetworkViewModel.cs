using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

public sealed partial class NetLabelValue : ObservableObject
{
    public NetLabelValue() { }
    public NetLabelValue(string label, string value, bool sensitive = false)
    {
        Label = label; Value = value; IsSensitive = sensitive;
    }
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>MACs, IPs, SSIDs and BSSIDs stay masked until tapped (screenshot-safe).</summary>
    public bool IsSensitive { get; set; }
    [ObservableProperty] public partial bool IsRevealed { get; set; }
    public string DisplayValue => IsSensitive && !IsRevealed ? "••••••••" : Value;
    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(DisplayValue));
    public void ToggleReveal() { if (IsSensitive) IsRevealed = !IsRevealed; }
}

public sealed partial class NetPropEdit : ObservableObject
{
    public NetAdvancedProp Prop { get; }
    /// <summary>Frozen at build time: elevation is a process-wide constant, so live
    /// re-evaluation would never change anything.</summary>
    public bool IsEditable { get; }
    public NetPropEdit(NetAdvancedProp prop, string proposed, bool editable = true)
    {
        Prop = prop;
        _proposed = proposed;
        IsEditable = editable;
    }

    private string _proposed;
    public string Proposed
    {
        get => _proposed;
        set
        {
            if (SetProperty(ref _proposed, value))
            {
                OnPropertyChanged(nameof(HasPendingChange));
                OnPropertyChanged(nameof(ValidationError));
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationVis));
                OnPropertyChanged(nameof(PendingText));
                OnPropertyChanged(nameof(ProposedNumber));
            }
        }
    }

    public bool HasPendingChange => !string.Equals(_proposed ?? "", Prop.Current ?? "", StringComparison.Ordinal);
    public string? ValidationError => NetParsing.ValidateValue(Prop, _proposed ?? "");
    public bool HasValidationError => ValidationError != null;
    public bool IsEnum => Prop.Type.Equals("enum", StringComparison.OrdinalIgnoreCase);
    public bool IsInt => Prop.Type.Equals("int", StringComparison.OrdinalIgnoreCase);
    public bool IsText => !IsEnum && !IsInt;
    public Microsoft.UI.Xaml.Visibility EnumVis => IsEnum ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility IntVis => IsInt ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility TextVis => IsText ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ValidationVis => HasValidationError ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ChangedVis => Prop.Changed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string PendingText => HasPendingChange ? $"New: {ProposedDesc}" : "";
    public double PropMinD => (double)(Prop.Min ?? 0);
    public double PropMaxD => (double)(Prop.Max ?? 100000);
    public double ProposedNumber
    {
        get => double.TryParse(_proposed, out var n) ? n : 0;
        set
        {
            Proposed = ((long)Math.Round(value)).ToString();
            OnPropertyChanged(nameof(ProposedNumber));
        }
    }
    public string CurrentDesc => NetParsing.OptionDesc(Prop.Options, Prop.Current);
    public string ProposedDesc => NetParsing.OptionDesc(Prop.Options, _proposed ?? "");
}

public sealed partial class NetPropGroup : ObservableObject
{
    public string Category { get; }
    public ObservableCollection<NetPropEdit> Items { get; } = new();
    public NetPropGroup(string category) => Category = category;
}

public sealed partial class SysRowVm : ObservableObject
{
    public NetParsing.SysRowDef Def { get; }
    public bool Editable { get; }
    public SysRowVm(NetParsing.SysRowDef def, string current, bool editable)
    {
        Def = def;
        _proposed = Canonical(current);
        _original = _proposed;
        Editable = editable;
    }

    /// <summary>Raised on every user edit. These rows have no Apply button — the
    /// owner writes the new value through as soon as it changes.</summary>
    public event Action<SysRowVm>? Edited;

    /// <summary>Set by the owner when an edit arrives while a write is still in
    /// flight, so the newest value is written again instead of being dropped.</summary>
    public bool ReapplyRequested { get; set; }

    private bool _suppressEdited;

    /// <summary>Windows reports "enabled" where the option list says "Enabled", and a
    /// combo bound by SelectedValue shows nothing when the two spellings differ.</summary>
    private string Canonical(string value) => NetParsing.CanonicalOption(Def, value);

    private string _original;
    private string _proposed = "";
    public string Proposed
    {
        get => _proposed;
        set
        {
            // A combo whose live value is not in its items reports null on load;
            // that is "nothing selected", not an edit to push to the machine.
            if (value is null) return;
            if (SetProperty(ref _proposed, value))
            {
                OnPropertyChanged(nameof(HasPendingChange));
                OnPropertyChanged(nameof(HasPendingVis));
                OnPropertyChanged(nameof(ValidationError));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorVis));
                if (!_suppressEdited) Edited?.Invoke(this);
            }
        }
    }

    public double ProposedNumber
    {
        get => double.TryParse(_proposed, out var n) ? n : 0;
        set
        {
            // An empty box reports NaN; keep the last good value instead of
            // turning it into an overflow that would trip the range check.
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            Proposed = ((long)Math.Round(value)).ToString();
        }
    }

    public bool HasPendingChange => !string.Equals(_proposed ?? "", _original ?? "", StringComparison.OrdinalIgnoreCase);
    public Microsoft.UI.Xaml.Visibility HasPendingVis => HasPendingChange
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string? ValidationError => NetParsing.ValidateSysValue(Def, _proposed ?? "");
    public bool HasError => ValidationError != null;
    // Only flag an error once the user actually edits the row; a stale or
    // unreadable current value (empty / not in the options list) must not
    // paint every row red on load.
    public Microsoft.UI.Xaml.Visibility ErrorVis => HasError && HasPendingChange
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool IsEnum => Def.Kind == NetParsing.SysKind.Enum;
    public Microsoft.UI.Xaml.Visibility EnumVis => IsEnum ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NumVis => IsEnum ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    private Microsoft.UI.Xaml.Visibility _rowVisible = Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility RowVisible
    {
        get => _rowVisible;
        set => SetProperty(ref _rowVisible, value);
    }

    public double MinD => (double)Def.Min;
    public double MaxD => (double)Def.Max;

    private string _applyState = "";
    public string ApplyState
    {
        get => _applyState;
        set
        {
            if (SetProperty(ref _applyState, value)) OnPropertyChanged(nameof(ApplyStateVisibility));
        }
    }
    public Microsoft.UI.Xaml.Visibility ApplyStateVisibility => string.IsNullOrEmpty(_applyState)
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private bool _isApplying;
    public bool IsApplying
    {
        get => _isApplying;
        set => SetProperty(ref _isApplying, value);
    }

    public void AcceptApplied(string readBack)
    {
        // Writing the read-back back into Proposed must not look like a new edit.
        _suppressEdited = true;
        try
        {
            _original = Canonical(readBack);
            Proposed = _original;
        }
        finally { _suppressEdited = false; }
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(HasPendingVis));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorVis));
    }
}

public sealed partial class SysSectionVm : ObservableObject
{
    public string Title { get; }
    public ObservableCollection<SysRowVm> Rows { get; } = new();

    /// <summary>What this group of settings covers, shown above its rows.</summary>
    public string Hint { get; }

    public SysSectionVm(string title, string hint = "")
    {
        Title = title;
        Hint = hint;
    }

    private string _statusText = "";

    /// <summary>Empty while nothing is staged, so the pill only appears when there is
    /// something to review.</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(StatusVis));
        }
    }

    public Microsoft.UI.Xaml.Visibility StatusVis => string.IsNullOrEmpty(_statusText)
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Rows in this section including the ones the filter hides.</summary>
    public int RowCount => Rows.Count;

    public void RefreshStatus()
    {
        int pending = Rows.Count(r => r.HasPendingChange);
        StatusText = pending == 0 ? "" : $"{pending} staged";
        OnPropertyChanged(nameof(RowCount));
    }
}

public sealed partial class NetworkViewModel : ObservableObject
{
    public ObservableCollection<NetAdapterInfo> Adapters { get; } = new();
    public ObservableCollection<NetPropGroup> PropGroups { get; } = new();
    public ObservableCollection<NetTcpEntry> TcpEntries { get; } = new();
    public ObservableCollection<NetTcpEntry> UdpEntries { get; } = new();
    public ObservableCollection<string> OffloadCaps { get; } = new();
    public ObservableCollection<NetLabelValue> RssRows { get; } = new();
    public ObservableCollection<SysSectionVm> SysSections { get; } = new();

    /// <summary>Which section the SysW side panel shows.</summary>
    [ObservableProperty] public partial int SysSectionIndex { get; set; }

    private static readonly SysSectionVm EmptySysSection = new("No sections");

    /// <summary>Never null: the W tab binds through it before the first read finishes.</summary>
    public SysSectionVm CurrentSysSection => SysSections.Count == 0
        ? EmptySysSection
        : SysSections[Math.Clamp(SysSectionIndex, 0, SysSections.Count - 1)];

    /// <summary>Shown when the filter hides every row of the current section.</summary>
    public Microsoft.UI.Xaml.Visibility SysEmptyVis =>
        CurrentSysSection.Rows.Any(r => r.RowVisible == Microsoft.UI.Xaml.Visibility.Visible)
            ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnSysSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentSysSection));
        OnPropertyChanged(nameof(SysEmptyVis));
    }
    public ObservableCollection<NetDoctorFinding> Findings { get; } = new();
    public ObservableCollection<NetSettingDiff> PendingDiffs { get; } = new();
    public ObservableCollection<NetSettingDiff> ApplyResults { get; } = new();
    public ObservableCollection<NetSettingDiff> CompareDiffs { get; } = new();
    public ObservableCollection<(string Text, string Path)> Snapshots { get; } = new();
    public ObservableCollection<NetLabelValue> OverviewRows { get; } = new();
    public ObservableCollection<NetLabelValue> DriverRows { get; } = new();
    public ObservableCollection<NetLabelValue> HardwareRows { get; } = new();
    public ObservableCollection<NetLabelValue> WifiRows { get; } = new();
    public ObservableCollection<NetPropGroup> OffloadGroups { get; } = new();
    public ObservableCollection<NetPropGroup> PowerGroups { get; } = new();
    public ObservableCollection<NetSettingDiff> ImportDiffs { get; } = new();

    [ObservableProperty] public partial NetAdapterInfo? SelectedAdapter { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool ShowHidden { get; set; }
    [ObservableProperty] public partial bool ShowDisconnected { get; set; }

    partial void OnShowDisconnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(FilteredAdapters));
        UpdateFilterNote();
    }

    private void UpdateFilterNote()
    {
        if (Adapters.Count > 0 && !FilteredAdapters.Any())
            StatusMessage = "No adapters match the current filters. Tick 'Show disconnected' or 'Show hidden / virtual'.";
        else if (StatusMessage.StartsWith("No adapters match"))
            StatusMessage = "";
    }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial bool IsElevated { get; set; }
    [ObservableProperty] public partial string KeepCountdownText { get; set; } = "";
    [ObservableProperty] public partial bool KeepUIVisible { get; set; }
    public Microsoft.UI.Xaml.Visibility KeepUIVisibility =>
        KeepUIVisible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    partial void OnKeepUIVisibleChanged(bool value) => OnPropertyChanged(nameof(KeepUIVisibility));
    [ObservableProperty] public partial string SpeedStatus { get; set; } = "Measures download and upload against Cloudflare's speed-test endpoint.";
    [ObservableProperty] public partial string SpeedDown { get; set; } = "—";
    [ObservableProperty] public partial string SpeedUp { get; set; } = "—";
    [ObservableProperty] public partial int SpeedProgress { get; set; }
    [ObservableProperty] public partial bool SpeedRunning { get; set; }
    [ObservableProperty] public partial NetAdapterInfo? CompareAdapter { get; set; }
    [ObservableProperty] public partial bool ConfirmRiskyEdit { get; set; }

    public bool CanEdit => IsElevated && SelectedAdapter != null;
    public bool IsRiskyTarget => SelectedAdapter != null &&
        (SelectedAdapter.Kind == NetAdapterKind.Virtual || SelectedAdapter.Kind == NetAdapterKind.Vpn || !SelectedAdapter.IsUp);
    partial void OnIsElevatedChanged(bool value)
    {
        OnPropertyChanged(nameof(ElevationText));
        OnPropertyChanged(nameof(CanEdit));
    }

    private NetSnapshot? _keepSnapshot;
    private bool _selecting;
    private NetAdapterInfo? _pendingSelect;
    private NetRevertState _revertState = NetRevertState.Idle;
    private int _keepSecondsLeft;
    private readonly List<NetStatSample> _series = new();
    private CancellationTokenSource? _speedCts;

    public event Action<string, string>? BannerRequested;
    public event Action? StatsUpdated;
    public void NotifyMessage(string title, string content) => BannerRequested?.Invoke(title, content);

    public IEnumerable<NetAdapterInfo> FilteredAdapters => Adapters.Where(a =>
        NetParsing.IsListVisible(a.Kind, a.IsUp, ShowHidden, ShowDisconnected) &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         a.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         a.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredAdapters));
        UpdateFilterNote();
    }
    partial void OnShowHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(FilteredAdapters));
        UpdateFilterNote();
    }

    public string ElevationText => IsElevated
        ? "Running elevated: editing enabled."
        : "Not elevated: read-only. Restart kaliteConfig as administrator to edit.";

    public IReadOnlyList<NetStatSample> Series => _series;
    public double SeriesMaxMbps { get; private set; } = 10;

    public NetworkViewModel() => IsElevated = NetEditService.IsElevated();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var keepGuid = SelectedAdapter?.Guid;
            var list = await NetAdapterService.ListAsync().ConfigureAwait(true);
            Adapters.Clear();
            foreach (var a in list) Adapters.Add(a);
            OnPropertyChanged(nameof(FilteredAdapters));
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Guid == keepGuid) ?? FilteredAdapters.FirstOrDefault();
            if (SelectedAdapter is null) StatusMessage = "No adapters found. If this machine truly has none, there is nothing to show.";
            else await SelectAdapterAsync(SelectedAdapter).ConfigureAwait(true);
            await LoadTcpAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { NotifyMessage("Refresh failed", ex.Message); }
        finally { IsBusy = false; }
    }

    partial void OnSelectedAdapterChanged(NetAdapterInfo? value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsRiskyTarget));
    }

    [RelayCommand]
    public async Task SelectAdapterAsync(NetAdapterInfo? adapter)
    {
        if (adapter is null) return;
        // Serialize: rapid re-selections used to interleave two fills (Clear, await,
        // Add...) and duplicate every row. A second request just re-runs once after.
        if (_selecting)
        {
            _pendingSelect = adapter;
            return;
        }
        _selecting = true;
        try
        {
            while (true)
            {
                if (_pendingSelect is not null)
                {
                    adapter = _pendingSelect;
                    _pendingSelect = null;
                }
                SelectedAdapter = adapter;
                PendingDiffs.Clear();
                ApplyResults.Clear();
        try
        {
            var props = await NetAdapterService.GetAdvancedAsync(adapter.RegistryKey).ConfigureAwait(true);
            RebuildGroups(props);
            FillRssRows(props);
            if (props.Count == 0)
                StatusMessage = $"{adapter.Name}: the driver exposes no advanced properties.";
            // Each group is independent: one unreadable section must not blank the
            // whole page, so they are isolated and reported individually.
            await StepAsync("overview", () => FillOverview(adapter)).ConfigureAwait(true);
            await StepAsync("driver", () => FillDriverAsync(adapter)).ConfigureAwait(true);
            await StepAsync("hardware", () => FillHardwareAsync(adapter)).ConfigureAwait(true);
            await StepAsync("wi-fi", () => FillWifiAsync(adapter)).ConfigureAwait(true);
            await StepAsync("snapshots", () => RefreshSnapshotsAsync(adapter)).ConfigureAwait(true);
            await StepAsync("offload", () => RefreshOffloadAsync()).ConfigureAwait(true);
            await StepAsync("system-wide", () => LoadSysAsync()).ConfigureAwait(true);
            await StepAsync("doctor", () => RefreshDoctorAsync()).ConfigureAwait(true);
            if (_firstReadError is not null)
            {
                NotifyMessage("Some settings could not be read", _firstReadError);
                _firstReadError = null;
            }
        }
        catch (Exception ex) { NotifyMessage("Could not read settings", ex.Message); }
        if (_pendingSelect is null) break;
    }
}
finally { _selecting = false; }
}

    private void RebuildGroups(List<NetAdvancedProp> props)
    {
        PropGroups.Clear();
        OffloadGroups.Clear();
        PowerGroups.Clear();
        foreach (var g in props.GroupBy(p => p.Category).OrderBy(g => g.Key))
        {
            var group = new NetPropGroup(g.Key);
            foreach (var p in g.OrderBy(p => p.DisplayName))
                group.Items.Add(new NetPropEdit(p, p.Current, CanEdit));
            PropGroups.Add(group);
            if (g.Key == "Offloads" || g.Key == "Interrupts & Buffers") OffloadGroups.Add(group);
            if (g.Key == "Power Saving" || g.Key == "Wake") PowerGroups.Add(group);
        }
    }

    private async Task FillOverview(NetAdapterInfo a)
    {
        OverviewRows.Clear();
        void Add(string l, string v, bool sensitive = false) => OverviewRows.Add(new NetLabelValue(l, v, sensitive));
        var lease = await NetAdapterService.GetDhcpLeaseAsync(a.Guid).ConfigureAwait(true);
        lease.TryGetValue("LeaseObtainedTime", out var lo);
        lease.TryGetValue("LeaseTerminatesTime", out var lt);
        Add("Status", a.Status);
        Add("Link speed", NetParsing.FormatSpeed(a.LinkSpeedBps));
        Add("MAC", string.IsNullOrEmpty(a.Mac) ? "Not reported" : a.Mac, sensitive: true);
        Add("Interface index", a.InterfaceIndex.ToString());
        Add("GUID", a.Guid);
        Add("MTU", string.IsNullOrEmpty(a.Mtu) ? "Not reported" : a.Mtu);
        Add("IPv4", a.IPv4.Count > 0 ? string.Join(", ", a.IPv4) : "None", sensitive: a.IPv4.Count > 0);
        Add("IPv6", a.IPv6.Count > 0 ? string.Join(", ", a.IPv6) : "None", sensitive: a.IPv6.Count > 0);
        Add("Gateway", a.Gateways.Count > 0 ? string.Join(", ", a.Gateways) : "None");
        Add("DNS", a.Dns.Count > 0 ? string.Join(", ", a.Dns) : "None");
        Add("DHCP", a.DhcpEnabled ? "On" + (string.IsNullOrEmpty(a.DhcpServer) ? "" : $" (server {a.DhcpServer})") : "Off (static)", sensitive: a.DhcpEnabled && !string.IsNullOrEmpty(a.DhcpServer));
        Add("Lease obtained", lo ?? "n/a (static)");
        Add("Lease expires", lt ?? "n/a (static)");
    }

    private async Task FillDriverAsync(NetAdapterInfo a)
    {
        DriverRows.Clear();
        void Add(string l, string v) => DriverRows.Add(new NetLabelValue(l, v));
        try
        {
            var d = await NetAdapterService.ReadDeviceValuesAsync(a.RegistryKey).ConfigureAwait(true);
            string Get(string k) => d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "Not reported by this driver.";
            Add("Description", Get("DriverDesc"));
            Add("Provider", Get("ProviderName"));
            Add("Version", Get("DriverVersion"));
            Add("Date", Get("DriverDate"));
            var (years, old) = NetParsing.DriverAge(d.TryGetValue("DriverDate", out var dd) ? dd : "", DateTime.UtcNow);
            Add("Driver age", old ? $"{years:0} years old — consider a vendor update if anything misbehaves." : (years <= 0 ? "Unknown" : $"{years:0.#} years old."));
            Add("INF", Get("InfPath"));
            Add("Matching device ID", Get("MatchingDeviceId"));
            Add("NDIS version", Get("NdisVersion"));
            string ndiService = await ReadNdiServiceAsync(a.RegistryKey).ConfigureAwait(true);
            Add("Service", ndiService);
            // ConfigureAwait(false) here would resume on a thread-pool thread and the
            // Add below would then touch the UI-bound DriverRows collection from the
            // wrong thread (COMException 0x8001010E), killing the whole refresh.
            string signer = await NetAdapterService.GetSignerAsync(
                ndiService.StartsWith("Not reported") ? null : ndiService).ConfigureAwait(true);
            Add("Digital signer", signer);
        }
        catch (Exception ex) { DriverRows.Add(new NetLabelValue("Error", ex.Message)); }
    }

    private static async Task<string> ReadNdiServiceAsync(string regKey)
    {
        if (string.IsNullOrEmpty(regKey)) return "Not reported by this driver.";
        return await Task.Run(() =>
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{regKey}\Ndi", false);
                var s = k?.GetValue("Service") as string;
                return string.IsNullOrWhiteSpace(s) ? "Not reported by this driver." : s;
            }
            catch { return "Not reported by this driver."; }
        }).ConfigureAwait(false);
    }

    private async Task FillHardwareAsync(NetAdapterInfo a)
    {
        HardwareRows.Clear();
        void Add(string l, string v) => HardwareRows.Add(new NetLabelValue(l, v));
        try
        {
            var d = await NetAdapterService.ReadDeviceValuesAsync(a.RegistryKey).ConfigureAwait(true);
            d.TryGetValue("MatchingDeviceId", out var mid);
            var (ven, dev, subsys, rev) = NetParsing.ParseVenDev(mid ?? "");
            Add("Vendor ID", string.IsNullOrEmpty(ven) ? "Not reported" : "0x" + ven);
            Add("Device ID", string.IsNullOrEmpty(dev) ? "Not reported" : "0x" + dev);
            Add("Subsystem", string.IsNullOrEmpty(subsys) ? "Not reported" : subsys);
            Add("Revision", string.IsNullOrEmpty(rev) ? "Not reported" : rev);
            Add("Matching device ID", string.IsNullOrEmpty(mid) ? "Not reported" : mid!);
            Add("Bus / slot location", "Not exposed in the driver key on this machine.");
            Add("PCIe link speed vs capability", "Not exposed without PCIe config-space reads; the Doctor flags it only when data exists.");
            Add("NUMA node", "Not reported by this driver.");
            Add("Interrupts", "Not reported by this driver (MSI-X assignment is owned by Windows).");
        }
        catch (Exception ex) { HardwareRows.Add(new NetLabelValue("Error", ex.Message)); }
    }

    private async Task FillWifiAsync(NetAdapterInfo a)
    {
        WifiRows.Clear();
        void Add(string l, string v, bool sensitive = false) => WifiRows.Add(new NetLabelValue(l, v, sensitive));
        if (a.Kind != NetAdapterKind.WiFi)
        {
            Add("Wireless", "This adapter is not wireless.");
            return;
        }
        try
        {
            var w = await NetAdapterService.GetWifiAsync(a.Name).ConfigureAwait(true);
            if (!w.Present)
            {
                Add("Wireless", w.StatusMessage);
                return;
            }
            Add("SSID", w.Ssid, sensitive: true); Add("BSSID", w.Bssid, sensitive: true); Add("Band", w.Band);
            Add("Channel", w.Channel); Add("PHY", w.Phy);
            Add("Signal", w.Signal); Add("RSSI", w.Rssi);
            Add("RX rate (Mbps)", w.RxRate); Add("TX rate (Mbps)", w.TxRate);
            Add("Security", w.Security);
        }
        catch (Exception ex) { WifiRows.Add(new NetLabelValue("Error", ex.Message)); }
    }

    private async Task RefreshSnapshotsAsync(NetAdapterInfo a)
    {
        Snapshots.Clear();
        try
        {
            var dir = NetAdapterService.SnapshotDir;
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var snap = await NetAdapterService.ReadSnapshotAsync(f).ConfigureAwait(true);
                    if (snap != null && snap.AdapterGuid == a.Guid)
                        Snapshots.Add(($"{snap.TakenUtc.ToLocalTime():g} ({snap.Values.Count} settings)", f));
                }
                catch { }
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task LoadTcpAsync()
    {
        try
        {
            var entries = await NetAdapterService.GetTcpGlobalAsync().ConfigureAwait(true);
            TcpEntries.Clear();
            foreach (var e in entries) TcpEntries.Add(e);
        }
        catch (Exception ex) { NotifyMessage("TCP settings unavailable", ex.Message); }
        try
        {
            var udp = await NetAdapterService.GetUdpGlobalAsync().ConfigureAwait(true);
            UdpEntries.Clear();
            foreach (var e in udp) UdpEntries.Add(e);
        }
        catch (Exception ex) { NotifyMessage("UDP settings unavailable", ex.Message); }
        await RefreshOffloadAsync().ConfigureAwait(true);
        await LoadSysAsync().ConfigureAwait(true);
    }

    /// <summary>Editable system-wide rows, grouped by section. RSS rows follow the
    /// selected adapter; the rest are machine-global. Only one section is on screen
    /// at a time (the SysW side panel picks it).</summary>
    public async Task LoadSysAsync()
    {
        try
        {
            var rows = await NetSysService.GetRowsAsync(SelectedAdapter?.Name ?? "").ConfigureAwait(true);
            SysSections.Clear();
            SysSectionIndex = 0;
            foreach (var g in rows.GroupBy(r => r.Def.Section))
            {
                var section = new SysSectionVm(g.Key, NetParsing.SysSectionHint(g.Key));
                foreach (var r in g)
                {
                    var row = new SysRowVm(r.Def, r.Current, IsElevated);
                    row.Edited += OnSysRowEdited;
                    section.Rows.Add(row);
                }
                section.RefreshStatus();
                SysSections.Add(section);
            }
            OnPropertyChanged(nameof(CurrentSysSection));
            // A re-read rebuilds every row, so the filter has to be applied again.
            ApplySysFilter(_sysChangedOnly);
            if (SysSections.Count == 0)
                NotifyMessage("System settings unavailable", "No editable system rows could be read.");
        }
        catch (Exception ex) { NotifyMessage("System settings unavailable", ex.Message); }
    }

    /// <summary>Re-reads every row from Windows, discarding staged values.</summary>
    [RelayCommand]
    public async Task ReReadSysAsync() => await LoadSysAsync().ConfigureAwait(true);

    public void ApplySysFilter(bool changedOnly)
    {
        _sysChangedOnly = changedOnly;
        foreach (var section in SysSections)
        {
            foreach (var row in section.Rows)
            {
                row.RowVisible = !changedOnly || row.HasPendingChange
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
            }
            section.RefreshStatus();
        }
        OnPropertyChanged(nameof(SysEmptyVis));
    }

    private bool _sysChangedOnly;

    /// <summary>Runs one read step in isolation. A failure is reported once (first
    /// failure wins) instead of aborting the remaining groups.</summary>
    private async Task StepAsync(string label, Func<Task> step)
    {
        try { await step().ConfigureAwait(true); }
        catch (Exception ex)
        {
            _firstReadError ??= $"{label}: {ex.Message}";
        }
    }

    private string? _firstReadError;

    /// <summary>One edit = one write. A second edit landing mid-write runs the write
    /// again afterwards so the last value the user picked is the one on the machine.</summary>
    private async void OnSysRowEdited(SysRowVm row)
    {
        // Show the staged count straight away, before the write lands.
        RefreshSectionStatusOf(row);
        if (row.IsApplying) { row.ReapplyRequested = true; return; }
        try
        {
            do
            {
                row.ReapplyRequested = false;
                await ApplySysRowAsync(row).ConfigureAwait(true);
            }
            while (row.ReapplyRequested);
        }
        catch (Exception ex) { row.ApplyState = "FAILED: " + ex.Message; }
        finally
        {
            RefreshSectionStatusOf(row);
            OnPropertyChanged(nameof(SysEmptyVis));
        }
    }

    private void RefreshSectionStatusOf(SysRowVm row)
    {
        foreach (var section in SysSections)
            if (section.Rows.Contains(row)) { section.RefreshStatus(); return; }
    }

    [RelayCommand]
    public async Task ApplySysRowAsync(SysRowVm? row)
    {
        if (row is null) return;
        // Nothing to write when the row still holds its read value, and an invalid
        // value is reported instead of being written.
        if (!row.HasPendingChange) return;
        if (row.ValidationError is string invalid) { row.ApplyState = "Not applied: " + invalid; return; }
        if (!IsElevated) { row.ApplyState = "Needs elevation."; return; }
        row.ApplyState = "";
        row.IsApplying = true;
        try
        {
            var (ok, readBack, message) = await NetSysService.ApplyRowAsync(
                row.Def, SelectedAdapter?.Name ?? "", row.Proposed).ConfigureAwait(true);
            if (ok)
            {
                row.AcceptApplied(readBack);
                row.ApplyState = "✓ " + message;
                RefreshSectionStatusOf(row);
            }
            else
            {
                row.ApplyState = "FAILED: " + message;
            }
        }
        catch (Exception ex) { row.ApplyState = "FAILED: " + ex.Message; }
        finally { row.IsApplying = false; }
    }

    private async Task RefreshOffloadAsync()
    {
        OffloadCaps.Clear();
        try
        {
            var all = await NetAdapterService.GetOffloadAsync().ConfigureAwait(true);
            var name = SelectedAdapter?.Name;
            var match = all.FirstOrDefault(o => string.Equals(o.Iface, name, StringComparison.OrdinalIgnoreCase));
            if (match.Caps is null || match.Caps.Count == 0)
            {
                OffloadCaps.Add(SelectedAdapter is null
                    ? "Pick an adapter to see its offload capabilities."
                    : $"No offload capabilities reported for {name ?? "this adapter"}.");
                return;
            }
            foreach (var c in match.Caps) OffloadCaps.Add(c);
        }
        catch (Exception ex) { OffloadCaps.Add("Offload query failed: " + ex.Message); }
    }

    private void FillRssRows(List<NetAdvancedProp> props)
    {
        RssRows.Clear();
        foreach (var p in props.Where(p =>
            p.Keyword.IndexOf("rss", StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Keyword.IndexOf("rsc", StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Keyword.IndexOf("numrssqueues", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p.DisplayName))
        {
            RssRows.Add(new NetLabelValue($"{p.DisplayName} ({p.Keyword})",
                $"{NetParsing.OptionDesc(p.Options, p.Current)}  [default: {NetParsing.OptionDesc(p.Options, p.Default)}]"));
        }
        if (RssRows.Count == 0)
            RssRows.Add(new NetLabelValue("RSS", "This driver exposes no RSS settings."));
    }

    [RelayCommand]
    public async Task RefreshDoctorAsync()
    {
        Findings.Clear();
        var a = SelectedAdapter;
        if (a is null) return;
        try
        {
            var props = PropGroups.SelectMany(g => g.Items).Select(e => e.Prop).ToList();
            string Get(string kw) => props.FirstOrDefault(p => p.Keyword == kw)?.Current ?? "";
            bool On(string kw) => Get(kw) == "1";
            var counters = await NetAdapterService.GetCountersAsync(a.Guid).ConfigureAwait(true);
            var siblings = Adapters.Select(x => (x.Name, JumboValue: "")).ToList();
            var facts = new NetAdapterFacts(
                a.Kind, a.Status, a.IsUp, a.LinkSpeedBps,
                GuessMaxLink(a),
                Get("*SpeedDuplex"), NetParsing.OptionDesc(
                    props.FirstOrDefault(p => p.Keyword == "*SpeedDuplex")?.Options ?? new List<NetAdvancedOption>(), Get("*SpeedDuplex")),
                On("*EEE"), On("EnableGreenEthernet"), On("PowerSavingMode"), null,
                Get("*RSS") == "1", Get("*NumRssQueues"), Environment.ProcessorCount,
                "", "", counters.InErr, counters.OutErr, counters.InDisc, counters.OutDisc,
                a.IPv6.Count > 0, a.Dns,
                Get("*JumboPacket"), siblings, false, await IsOnlyUpPhysicalAsync().ConfigureAwait(true));
            foreach (var f in NetParsing.DoctorRules(facts)) Findings.Add(f);
            var driver = await NetAdapterService.ReadDeviceValuesAsync(a.RegistryKey).ConfigureAwait(true);
            driver.TryGetValue("ProviderName", out var prov);
            driver.TryGetValue("DriverDate", out var date);
            if (!string.IsNullOrEmpty(prov) || !string.IsNullOrEmpty(date))
            {
                var extra = new NetAdapterFacts(facts.Kind, facts.Status, facts.IsUp, facts.LinkSpeedBps, facts.MaxLinkSpeedBps,
                    facts.SpeedDuplexValue, facts.SpeedDuplexDesc, facts.EeeOn, facts.GreenEthernetOn, facts.PowerSavingOn,
                    facts.AllowComputerToTurnOff, facts.RssEnabled, facts.NumRssQueues, facts.ProcessorCount,
                    prov ?? "", date ?? "", facts.InErrors, facts.OutErrors, facts.InDiscards, facts.OutDiscards,
                    facts.HasIPv6, facts.Dns, facts.JumboValue, facts.SiblingJumbos, facts.PcieBelowCapability, facts.IsOnlyUpPhysical);
                Findings.Clear();
                foreach (var f in NetParsing.DoctorRules(extra)) Findings.Add(f);
            }
        }
        catch (Exception ex) { NotifyMessage("Diagnostics failed", ex.Message); }
    }

    private static long GuessMaxLink(NetAdapterInfo a)
    {
        var d = (a.Description ?? "").ToLowerInvariant();
        if (d.Contains("2.5g")) return 2500000000;
        if (d.Contains("5g ") || d.Contains("5gb")) return 5000000000;
        if (d.Contains("10g")) return 10000000000;
        if (d.Contains("gigabit") || d.Contains("1.0 g")) return 1000000000;
        if (a.Kind == NetAdapterKind.WiFi) return 0;
        return a.LinkSpeedBps;
    }

    private async Task<bool> IsOnlyUpPhysicalAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return Adapters.Count(a => a.IsUp && (a.Kind == NetAdapterKind.Ethernet || a.Kind == NetAdapterKind.WiFi)) <= 1;
    }

    public void RebuildPendingDiffs()
    {
        PendingDiffs.Clear();
        foreach (var g in PropGroups)
            foreach (var e in g.Items)
                if (e.HasPendingChange && e.ValidationError is null)
                    PendingDiffs.Add(new NetSettingDiff(e.Prop.Keyword, e.Prop.DisplayName, e.Prop.Current, e.Proposed));
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        var a = SelectedAdapter;
        if (a is null) return;
        RebuildPendingDiffs();
        if (PendingDiffs.Count == 0) { NotifyMessage("Nothing to apply", "No setting differs from its current value."); return; }
        if (!IsElevated) { NotifyMessage("Read-only", NetEditService.ElevationMessage($"adapter {a.Name} settings")); return; }
        if (a.Kind == NetAdapterKind.Virtual || a.Kind == NetAdapterKind.Vpn)
        {
            NotifyMessage("Confirm virtual adapter", "That safety check lives in the Apply dialog on the page.");
            return;
        }
        try
        {
            var props = PropGroups.SelectMany(g => g.Items).Select(e => e.Prop).ToList();
            var snap = await NetEditService.SnapshotAsync(a.Guid, a.Name).ConfigureAwait(true);
            var wanted = PendingDiffs.ToDictionary(d => d.Keyword, d => d.To, StringComparer.OrdinalIgnoreCase);
            var result = await NetEditService.ApplyAsync(a.Guid, a.Name, a.RegistryKey, props, wanted).ConfigureAwait(true);
            ApplyResults.Clear();
            foreach (var p in result.PerSetting)
                ApplyResults.Add(new NetSettingDiff(p.Keyword, p.Keyword, p.Applied ? "applied" : "FAILED", p.Reason));
            NotifyMessage(result.Ok ? "Applied" : "Apply finished with failures", result.Message);
            if (result.Ok)
            {
                _keepSnapshot = snap;
                ArmKeepCountdown();
                await SelectAdapterAsync(a).ConfigureAwait(true);
            }
        }
        catch (Exception ex) { NotifyMessage("Apply failed", ex.Message); }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _keepTimer;
    private void ArmKeepCountdown()
    {
        _revertState = NetParsing.RevertAdvance(NetRevertState.Idle, "arm");
        _keepSecondsLeft = 20;
        KeepUIVisible = true;
        UpdateKeepText();
        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _keepTimer?.Stop();
        _keepTimer = q.CreateTimer();
        _keepTimer.Interval = TimeSpan.FromSeconds(1);
        _keepTimer.Tick += KeepTick;
        _keepTimer.Start();
    }

    private async void KeepTick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        _keepSecondsLeft--;
        var a = SelectedAdapter;
        if (a != null && !await IsStillUpAsync(a.Guid).ConfigureAwait(true))
        {
            _revertState = NetParsing.RevertAdvance(_revertState, "linklost");
            await RevertAsync().ConfigureAwait(true);
            return;
        }
        if (_keepSecondsLeft <= 0)
        {
            _revertState = NetParsing.RevertAdvance(_revertState, "timeout");
            await RevertAsync().ConfigureAwait(true);
            return;
        }
        UpdateKeepText();
    }

    private static async Task<bool> IsStillUpAsync(string guid)
    {
        try
        {
            var list = await NetAdapterService.ListAsync().ConfigureAwait(false);
            return list.Any(x => x.Guid == guid && x.IsUp);
        }
        catch { return true; }
    }

    private void UpdateKeepText() =>
        KeepCountdownText = $"Keep these changes? {_keepSecondsLeft}s — auto-revert if unconfirmed or the link drops.";

    [RelayCommand]
    public void KeepChanges()
    {
        _revertState = NetParsing.RevertAdvance(_revertState, "confirm");
        _keepTimer?.Stop();
        KeepUIVisible = false;
        NotifyMessage("Kept", "Changes confirmed and kept.");
    }

    [RelayCommand]
    public async Task RevertAsync()
    {
        _keepTimer?.Stop();
        KeepUIVisible = false;
        if (_keepSnapshot is null || SelectedAdapter is null) return;
        _revertState = NetParsing.RevertAdvance(_revertState, "revert");
        try
        {
            var r = await NetEditService.RollbackAsync(_keepSnapshot, SelectedAdapter.Name, SelectedAdapter.RegistryKey).ConfigureAwait(true);
            NotifyMessage(r.Ok ? "Reverted" : "Revert finished with failures", r.Message);
            await SelectAdapterAsync(SelectedAdapter).ConfigureAwait(true);
        }
        catch (Exception ex) { NotifyMessage("Revert failed", ex.Message); }
    }

    [RelayCommand]
    public async Task ResetDefaultsAsync()
    {
        var a = SelectedAdapter;
        if (a is null) return;
        if (!IsElevated) { NotifyMessage("Read-only", NetEditService.ElevationMessage($"adapter {a.Name} settings")); return; }
        try
        {
            await NetEditService.SnapshotAsync(a.Guid, a.Name).ConfigureAwait(true);
            var r = await NetEditService.ResetToDefaultsAsync(a.Guid, a.Name, a.RegistryKey).ConfigureAwait(true);
            NotifyMessage(r.Ok ? "Defaults restored" : "Reset finished with failures", r.Message);
            await SelectAdapterAsync(a).ConfigureAwait(true);
        }
        catch (Exception ex) { NotifyMessage("Reset failed", ex.Message); }
    }

    [RelayCommand]
    public async Task TickStatsAsync()
    {
        var a = SelectedAdapter;
        if (a is null) return;
        try
        {
            var s = await NetAdapterService.GetStatsAsync(a.Guid).ConfigureAwait(true);
            if (s is null) return;
            _series.Add(s);
            while (_series.Count > 180) _series.RemoveAt(0);
            if (_series.Count >= 2)
            {
                double peak = 0;
                for (int i = 1; i < _series.Count; i++)
                {
                    double dt = (_series[i].AtUtc - _series[i - 1].AtUtc).TotalSeconds;
                    if (dt <= 0) continue;
                    peak = Math.Max(peak, 8.0 * (_series[i].RxBytes - _series[i - 1].RxBytes) / dt / 1e6);
                    peak = Math.Max(peak, 8.0 * (_series[i].TxBytes - _series[i - 1].TxBytes) / dt / 1e6);
                }
                SeriesMaxMbps = Math.Max(10, peak * 1.2);
            }
            StatsUpdated?.Invoke();
        }
        catch { }
    }

    [RelayCommand]
    public async Task StartSpeedTestAsync()
    {
        if (SpeedRunning) return;
        _speedCts?.Cancel();
        _speedCts = new CancellationTokenSource();
        var ct = _speedCts.Token;
        SpeedRunning = true;
        SpeedProgress = 0;
        SpeedDown = "—";
        SpeedUp = "—";
        SpeedStatus = "Running: downloading…";
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            var result = await NetSpeedTest.RunAsync((down, up, frac) =>
            {
                void Apply()
                {
                    SpeedDown = NetSpeedTest.FormatMbps(down);
                    if (up > 0) SpeedUp = NetSpeedTest.FormatMbps(up);
                    SpeedProgress = (int)(100.0 * frac);
                    if (frac >= 0.6 && SpeedStatus.StartsWith("Running: downloading"))
                        SpeedStatus = "Running: uploading…";
                }
                if (ui is null) Apply();
                else try { ui.TryEnqueue(Apply); } catch { }
            }, ct).ConfigureAwait(true);
            SpeedDown = NetSpeedTest.FormatMbps(result.DownMbps);
            SpeedUp = NetSpeedTest.FormatMbps(result.UpMbps);
            SpeedProgress = 100;
            SpeedStatus = $"Done: ↓ {SpeedDown} / ↑ {SpeedUp}.";
        }
        catch (OperationCanceledException) { SpeedStatus = "Speed test cancelled."; }
        catch (Exception ex) { SpeedStatus = "Speed test failed: " + ex.Message; }
        finally { SpeedRunning = false; }
    }

    [RelayCommand]
    public void CancelSpeedTest() => _speedCts?.Cancel();

    [RelayCommand]
    public void StageFix(NetDoctorFinding? finding)
    {
        if (finding?.FixKeyword is null || SelectedAdapter is null) return;
        var edit = PropGroups.SelectMany(g => g.Items).FirstOrDefault(e => e.Prop.Keyword == finding.FixKeyword);
        if (edit is null) { NotifyMessage("Fix unavailable", $"The current driver does not expose {finding.FixKeyword}."); return; }
        edit.Proposed = finding.FixValue ?? "";
        RebuildPendingDiffs();
        NotifyMessage("Fix staged", $"{finding.FixDescription} Review the diff on the Advanced tab, then Apply.");
    }

    [RelayCommand]
    public async Task RollbackSnapshotAsync(string? snapshotPath)
    {
        var a = SelectedAdapter;
        if (a is null || string.IsNullOrEmpty(snapshotPath)) return;
        if (!IsElevated) { NotifyMessage("Read-only", NetEditService.ElevationMessage($"adapter {a.Name} settings")); return; }
        try
        {
            var snap = await NetAdapterService.ReadSnapshotAsync(snapshotPath).ConfigureAwait(true);
            if (snap is null) { NotifyMessage("Rollback failed", "Could not read that snapshot file."); return; }
            var r = await NetEditService.RollbackAsync(snap, a.Name, a.RegistryKey).ConfigureAwait(true);
            NotifyMessage(r.Ok ? "Reverted" : "Revert finished with failures", r.Message);
            await SelectAdapterAsync(a).ConfigureAwait(true);
        }
        catch (Exception ex) { NotifyMessage("Rollback failed", ex.Message); }
    }

    [RelayCommand]
    public async Task ImportProfileAsync(string profilePath)
    {
        var a = SelectedAdapter;
        if (a is null) return;
        ImportDiffs.Clear();
        try
        {
            var json = await System.IO.File.ReadAllTextAsync(profilePath).ConfigureAwait(true);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Values", out var values)) { NotifyMessage("Import failed", "That file has no \"Values\" object."); return; }
            var live = PropGroups.SelectMany(g => g.Items).ToDictionary(e => e.Prop.Keyword, e => e, StringComparer.OrdinalIgnoreCase);
            foreach (var prop in values.EnumerateObject())
            {
                if (!live.TryGetValue(prop.Name, out var edit))
                {
                    ImportDiffs.Add(new NetSettingDiff(prop.Name, prop.Name, "(not in this driver)", prop.Value.GetString() ?? ""));
                    continue;
                }
                var want = prop.Value.GetString() ?? "";
                ImportDiffs.Add(new NetSettingDiff(prop.Name, edit.Prop.DisplayName, edit.Prop.Current, want));
            }
            NotifyMessage("Import preview ready", $"{ImportDiffs.Count} entr(ies) below. Press 'Stage import' to load them into the editors, then Apply.");
        }
        catch (Exception ex) { NotifyMessage("Import failed", ex.Message); }
    }

    [RelayCommand]
    public void StageImport()
    {
        int staged = 0, skipped = 0;
        var live = PropGroups.SelectMany(g => g.Items).ToDictionary(e => e.Prop.Keyword, e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var d in ImportDiffs)
        {
            if (live.TryGetValue(d.Keyword, out var edit))
            {
                var err = NetParsing.ValidateValue(edit.Prop, d.To);
                if (err is null) { edit.Proposed = d.To; staged++; }
                else skipped++;
            }
            else skipped++;
        }
        RebuildPendingDiffs();
        NotifyMessage("Import staged", $"{staged} value(s) staged, {skipped} skipped. Review the diff, then Apply.");
    }

    [RelayCommand]
    public async Task CompareAsync()
    {
        CompareDiffs.Clear();
        var a = SelectedAdapter;
        var b = CompareAdapter;
        if (a is null || b is null) { NotifyMessage("Compare needs two", "Pick an adapter on both sides."); return; }
        try
        {
            var pa = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(true);
            var pb = await NetAdapterService.GetAdvancedAsync(b.RegistryKey).ConfigureAwait(true);
            var da = pa.ToDictionary(p => p.Keyword, p => p.Current, StringComparer.OrdinalIgnoreCase);
            var db = pb.ToDictionary(p => p.Keyword, p => p.Current, StringComparer.OrdinalIgnoreCase);
            var keys = da.Keys.Union(db.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            foreach (var k in keys)
            {
                da.TryGetValue(k, out var va);
                db.TryGetValue(k, out var vb);
                if (!string.Equals(va ?? "", vb ?? "", StringComparison.Ordinal))
                    CompareDiffs.Add(new NetSettingDiff(k, k, $"{a.Name}: {va ?? "(absent)"}", $"{b.Name}: {vb ?? "(absent)"}"));
            }
            if (CompareDiffs.Count == 0) NotifyMessage("Compare", "No differences in driver settings.");
        }
        catch (Exception ex) { NotifyMessage("Compare failed", ex.Message); }
    }

    public async Task<string> ExportAdapterAsync(NetAdapterInfo a, string path, string format)
    {
        if (format == "csv")
        {
            var props = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(false);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Keyword,DisplayName,Current,Default,Changed");
            foreach (var p in props)
                sb.AppendLine($"\"{p.Keyword}\",\"{p.DisplayName}\",\"{p.Current}\",\"{p.Default}\",{p.Changed}");
            await System.IO.File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
        }
        else if (format == "txt")
        {
            await System.IO.File.WriteAllTextAsync(path, await ExportTextAsync(a).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            var props = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(false);
            var payload = new
            {
                Adapter = a.Name, Description = a.Description, ExportedUtc = DateTime.UtcNow,
                Values = props.ToDictionary(p => p.Keyword, p => p.Current),
            };
            await System.IO.File.WriteAllTextAsync(path,
                System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        }
        return path;
    }

    public async Task<string> ExportTextAsync(NetAdapterInfo a)
    {
        var props = await NetAdapterService.GetAdvancedAsync(a.RegistryKey).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Network report — {a.Name} ({a.Description})");
        sb.AppendLine($"Status: {a.Status}, link {NetParsing.FormatSpeed(a.LinkSpeedBps)}, MAC {a.Mac}");
        sb.AppendLine($"IPv4: {string.Join(", ", a.IPv4)}; Gateway: {string.Join(", ", a.Gateways)}; DNS: {string.Join(", ", a.Dns)}");
        sb.AppendLine("Advanced (keyword = current [default]):");
        foreach (var p in props)
            sb.AppendLine($"  {p.Keyword} = {p.Current} [{p.Default}]  ({p.DisplayName})");
        return sb.ToString();
    }
}


