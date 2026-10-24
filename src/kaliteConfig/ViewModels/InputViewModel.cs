using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

public sealed partial class MouseDeviceRow : ObservableObject
{
    public MouseDeviceRow(Input.MouseDevice device)
    {
        Device = device;
    }

    public Input.MouseDevice Device { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MeasuredLine { get; set; } = string.Empty;
}

public sealed partial class InputViewModel : ObservableObject, IDisposable
{
    private readonly Input.IMouseDiscoveryService _discovery;
    private readonly Input.IMouseCapabilityProvider _capabilities;
    private readonly Input.IWindowsMouseSettingsService _windows;
    private readonly Input.IMouseTestService _test;
    private readonly Input.IMouseLog _log;
    private readonly Input.IMousePrefStore _prefs;
    private readonly DispatcherQueue _ui;
    private bool _disposed;
    private Input.WindowsMouseState _original = new();
    private Input.ReportRateResult? _lastResult;
    private readonly List<double> _clickTimes = new();
    private bool _testVisible;
    private double _roadSeconds;
    private long _roadReports;
    private MouseDeviceRow? _fallbackRow;

    public InputViewModel(
        Input.IMouseDiscoveryService discovery,
        Input.IMouseCapabilityProvider capabilities,
        Input.IWindowsMouseSettingsService windows,
        Input.IMouseTestService test,
        Input.IMouseLog log,
        Input.IMousePrefStore prefs)
    {
        _discovery = discovery;
        _capabilities = capabilities;
        _windows = windows;
        _test = test;
        _log = log;
        _prefs = prefs;
        _ui = DispatcherQueue.GetForCurrentThread();
        _test.Progress += OnTestProgress;
        _test.DeviceRemoved += OnTestDeviceRemoved;
        _discovery.Changed += OnDevicesChanged;
        ReapplyOnConnect = _prefs.Data.ReapplyOnConnect;
    }

    public Input.IMouseLog Log => _log;

    [ObservableProperty]
    public partial ObservableCollection<MouseDeviceRow> Devices { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<MouseDeviceRow> IgnoredDevices { get; set; } = new();

    [ObservableProperty]
    public partial MouseDeviceRow? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<Input.MouseCapability> Capabilities { get; set; } = new();

    [ObservableProperty]
    public partial Input.MouseCapability? SelectedCapability { get; set; }

    [ObservableProperty]
    public partial bool HasSupportedDevices { get; set; }

    [ObservableProperty]
    public partial string VendorBanner { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowVendorBanner { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; } = 10;

    [ObservableProperty]
    public partial bool Precision { get; set; }

    [ObservableProperty]
    public partial bool Trails { get; set; }

    [ObservableProperty]
    public partial double WheelLines { get; set; } = 3;

    [ObservableProperty]
    public partial double WheelChars { get; set; } = 1;

    [ObservableProperty]
    public partial bool PrimaryLeft { get; set; } = true;

    [ObservableProperty]
    public partial double DoubleClickMs { get; set; } = 500;

    [ObservableProperty]
    public partial bool SnapDefault { get; set; }

    [ObservableProperty]
    public partial bool ClickLock { get; set; }

    [ObservableProperty]
    public partial double ClickLockMs { get; set; } = 1200;

    [ObservableProperty]
    public partial string WindowsOriginalText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TestRunning { get; set; }

    [ObservableProperty]
    public partial string TestStatusText { get; set; } = "Move the mouse";

    [ObservableProperty]
    public partial string TestResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MismatchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveButtonsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveWheelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveSpeedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReportMeasuredLine { get; set; } = "Not measured yet.";

    public event Action<double>? RoadUpdate;

    [ObservableProperty]
    public partial double DpiInches { get; set; } = 1.0;

    [ObservableProperty]
    public partial string DpiResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClickTestText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Nickname { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ReapplyOnConnect { get; set; }

    [ObservableProperty]
    public partial string InfoBrand { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoVidPid { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoSerial { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoConnection { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoReceiver { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoVendor { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoMemory { get; set; } = string.Empty;

    public void Open()
    {
        _testVisible = true;
        _discovery.StartNotifications();
        RefreshDevices();
    }

    public void Close()
    {
        _testVisible = false;
        try { StopTestInternal(); } catch { }
        _discovery.StopNotifications();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _test.Progress -= OnTestProgress;
        _test.DeviceRemoved -= OnTestDeviceRemoved;
        _discovery.Changed -= OnDevicesChanged;
        Close();
    }

    private void OnDevicesChanged()
    {
        _ui.TryEnqueue(() =>
        {
            UpdateDeviceList();
            CheckDrift();
            if (ReapplyOnConnect) _ = ReapplyAsync();
        });
    }

    public void RefreshDevices()
    {
        try { _discovery.Refresh(); }
        catch { }
        UpdateDeviceList();
    }

    private void UpdateDeviceList()
    {
        var supported = new List<MouseDeviceRow>();
        var ignored = new List<MouseDeviceRow>();
        foreach (var device in _discovery.Devices)
        {
            if (_prefs.Data.ForceShow.TryGetValue(device.StableId, out bool force) && force)
                device.ForceShow = true;
            if (_prefs.Data.Nicknames.TryGetValue(device.StableId, out string? nick) && !string.IsNullOrWhiteSpace(nick))
                device.Nickname = nick;
            var row = new MouseDeviceRow(device)
            {
                DisplayName = device.Nickname.Length > 0 ? device.Nickname : device.DisplayName,
                ConnectionText = ConnectionText(device.Connection),
            };
            if (device.Class == Input.MouseClass.Supported || device.ForceShow) supported.Add(row);
            else ignored.Add(row);
        }
        _ui.TryEnqueue(() =>
        {
            Devices.Clear();
            foreach (var row in supported) Devices.Add(row);
            IgnoredDevices.Clear();
            foreach (var row in ignored) IgnoredDevices.Add(row);
            HasSupportedDevices = Devices.Count > 0;
            if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
                SelectedDevice = Devices.FirstOrDefault();
            else
                LoadSelected();
            _fallbackRow = null;
            if (SelectedDevice is null)
                ShowFallbackInfo(IgnoredDevices.FirstOrDefault());
        });
    }

    partial void OnSelectedDeviceChanged(MouseDeviceRow? value)
    {
        _fallbackRow = null;
        LoadSelected();
    }

    private void ShowFallbackInfo(MouseDeviceRow? row)
    {
        _fallbackRow = row;
        Capabilities.Clear();
        SelectedCapability = null;
        if (row is null) return;
        Nickname = row.Device.Nickname;
        InfoBrand = row.Device.BrandText.Length > 0 ? row.Device.BrandText : "Unknown brand";
        InfoModel = row.Device.Product.Length > 0 ? row.Device.Product : "Unknown model";
        InfoVidPid = "VID " + row.Device.VendorId.ToString("X4") + ", PID " + row.Device.ProductId.ToString("X4");
        InfoSerial = row.Device.Serial.Length > 0 ? row.Device.Serial : "Not exposed";
        InfoConnection = ConnectionText(row.Device.Connection);
        InfoReceiver = row.Device.BehindReceiver ? "Linked to a wireless receiver" : "Direct connection";
        InfoVendor = row.Device.VendorSoftwarePresent ? row.Device.VendorSoftwareName : "None detected";
        InfoReason = row.Device.ClassReason;
        InfoMemory = "Can't read from this mouse. No verified module exists for it.";
    }

    private void LoadSelected()
    {
        var row = SelectedDevice;
        if (row is null)
        {
            Capabilities.Clear();
            return;
        }
        Nickname = row.Device.Nickname;
        var caps = _capabilities.ForDevice(row.Device);
        Capabilities.Clear();
        foreach (var cap in caps) Capabilities.Add(cap);
        SelectedCapability = Capabilities.FirstOrDefault(c => c.Kind == Input.CapabilityKind.DeviceInfo) ?? Capabilities.FirstOrDefault();
        ShowVendorBanner = row.Device.VendorSoftwarePresent;
        VendorBanner = row.Device.VendorSoftwarePresent
            ? row.Device.VendorSoftwareName + " is running and may overwrite changes made here"
            : string.Empty;
        InfoBrand = row.Device.BrandText.Length > 0 ? row.Device.BrandText : "Unknown brand";
        InfoModel = row.Device.Product.Length > 0 ? row.Device.Product : "Unknown model";
        InfoVidPid = "VID " + row.Device.VendorId.ToString("X4") + ", PID " + row.Device.ProductId.ToString("X4");
        InfoSerial = row.Device.Serial.Length > 0 ? row.Device.Serial : "Not exposed";
        InfoConnection = ConnectionText(row.Device.Connection);
        InfoReceiver = row.Device.BehindReceiver ? "Linked to a wireless receiver" : "Direct connection";
        InfoVendor = row.Device.VendorSoftwarePresent ? row.Device.VendorSoftwareName : "None detected";
        InfoReason = row.Device.ClassReason;
        InfoMemory = "Can't read from this mouse. No verified module exists for it.";
        LoadWindows();
    }

    public static string ConnectionText(Input.MouseConnection connection) => connection switch
    {
        Input.MouseConnection.Usb => "USB",
        Input.MouseConnection.Receiver24Ghz => "2.4 GHz receiver",
        Input.MouseConnection.Bluetooth => "Bluetooth",
        Input.MouseConnection.Other => "Other",
        _ => "Unknown",
    };

    private void LoadWindows()
    {
        try
        {
            _original = _windows.Read();
        }
        catch
        {
            _original = new Input.WindowsMouseState();
        }
        Speed = _original.PointerSpeed;
        Precision = _original.Precision;
        Trails = _original.Trails;
        WheelLines = _original.WheelLines;
        WheelChars = _original.WheelChars;
        PrimaryLeft = !_original.Swapped;
        DoubleClickMs = _original.DoubleClickMs;
        SnapDefault = _original.SnapDefault;
        ClickLock = _original.ClickLock;
        ClickLockMs = _original.ClickLockMs;
        WindowsOriginalText = "Original: speed " + _original.PointerSpeed
            + ", lines " + _original.WheelLines
            + ", double-click " + _original.DoubleClickMs + " ms.";
        WindowsResultText = string.Empty;
    }

    private string DeviceId() => SelectedDevice?.Device.StableId ?? "windows";
    private string DeviceName() => SelectedDevice?.DisplayName ?? "Mouse";

    public async Task ApplyWindowsAsync()
    {
        var messages = new List<string>();
        int speed = (int)Math.Round(Speed);
        int lines = (int)Math.Round(WheelLines);
        int chars = (int)Math.Round(WheelChars);
        uint clickMs = (uint)Math.Round(DoubleClickMs);
        uint lockMs = (uint)Math.Round(ClickLockMs);
        if (speed != _original.PointerSpeed)
            messages.Add((await _windows.ApplySpeedAsync(DeviceId(), DeviceName(), _original.PointerSpeed, speed).ConfigureAwait(false)).Message);
        if (Precision != _original.Precision)
            messages.Add((await _windows.ApplyPrecisionAsync(DeviceId(), DeviceName(), _original.Precision, Precision).ConfigureAwait(false)).Message);
        if (Trails != _original.Trails)
            messages.Add((await _windows.ApplyTrailsAsync(DeviceId(), DeviceName(), _original.Trails, Trails).ConfigureAwait(false)).Message);
        if (lines != _original.WheelLines)
            messages.Add((await _windows.ApplyWheelLinesAsync(DeviceId(), DeviceName(), _original.WheelLines, lines).ConfigureAwait(false)).Message);
        if (chars != _original.WheelChars)
            messages.Add((await _windows.ApplyWheelCharsAsync(DeviceId(), DeviceName(), _original.WheelChars, chars).ConfigureAwait(false)).Message);
        if (PrimaryLeft == _original.Swapped)
            messages.Add((await _windows.ApplySwapAsync(DeviceId(), DeviceName(), _original.Swapped, !PrimaryLeft).ConfigureAwait(false)).Message);
        if (clickMs != _original.DoubleClickMs)
            messages.Add((await _windows.ApplyDoubleClickAsync(DeviceId(), DeviceName(), _original.DoubleClickMs, clickMs).ConfigureAwait(false)).Message);
        if (SnapDefault != _original.SnapDefault)
            messages.Add((await _windows.ApplySnapAsync(DeviceId(), DeviceName(), _original.SnapDefault, SnapDefault).ConfigureAwait(false)).Message);
        if (ClickLock != _original.ClickLock)
            messages.Add((await _windows.ApplyClickLockAsync(DeviceId(), DeviceName(), _original.ClickLock, ClickLock).ConfigureAwait(false)).Message);
        if (lockMs != _original.ClickLockMs)
            messages.Add((await _windows.ApplyClickLockTimeAsync(DeviceId(), DeviceName(), _original.ClickLockMs, lockMs).ConfigureAwait(false)).Message);
        if (messages.Count == 0) messages.Add("No changes to apply.");
        RememberApplied();
        LoadWindows();
        WindowsResultText = string.Join(" ", messages);
    }

    private void RememberApplied()
    {
        try
        {
            var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pointer speed"] = Speed.ToString(),
                ["Scroll lines"] = WheelLines.ToString(),
                ["Double-click speed"] = DoubleClickMs.ToString(),
            };
            _prefs.Data.Applied[DeviceId()] = applied;
            _prefs.Save();
        }
        catch { }
    }

    public async Task ResetWindowsSectionAsync()
    {
        LoadWindows();
        WindowsResultText = "Section reset to the current values.";
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task RevertWindowsAsync()
    {
        var outcome = await _windows.RestoreAllAsync(DeviceId(), DeviceName()).ConfigureAwait(false);
        LoadWindows();
        WindowsResultText = outcome.Message;
    }

    public void SaveNickname()
    {
        var row = SelectedDevice ?? _fallbackRow;
        if (row is null) return;
        string nick = (Nickname ?? string.Empty).Trim();
        row.Device.Nickname = nick;
        row.DisplayName = nick.Length > 0 ? nick : row.Device.DisplayName;
        try
        {
            if (nick.Length > 0) _prefs.Data.Nicknames[row.Device.StableId] = nick;
            else _prefs.Data.Nicknames.Remove(row.Device.StableId);
            _prefs.Save();
        }
        catch { }
    }

    public void ShowIgnoredAnyway(MouseDeviceRow? row)
    {
        if (row is null) return;
        row.Device.ForceShow = true;
        try
        {
            _prefs.Data.ForceShow[row.Device.StableId] = true;
            _prefs.Save();
        }
        catch { }
        RefreshDevices();
    }

    partial void OnReapplyOnConnectChanged(bool value)
    {
        try
        {
            _prefs.Data.ReapplyOnConnect = value;
            _prefs.Save();
        }
        catch { }
    }

    private async Task ReapplyAsync()
    {
        foreach (var row in Devices)
        {
            if (!_prefs.Data.Applied.TryGetValue(row.Device.StableId, out var applied)) continue;
            foreach (var pair in applied)
            {
                try
                {
                    if (pair.Key == "Pointer speed" && int.TryParse(pair.Value, out int speed))
                        await _windows.ApplySpeedAsync(row.Device.StableId, row.DisplayName, speed, speed).ConfigureAwait(false);
                    else if (pair.Key == "Scroll lines" && int.TryParse(pair.Value, out int lines))
                        await _windows.ApplyWheelLinesAsync(row.Device.StableId, row.DisplayName, lines, lines).ConfigureAwait(false);
                    else if (pair.Key == "Double-click speed" && uint.TryParse(pair.Value, out uint ms))
                        await _windows.ApplyDoubleClickAsync(row.Device.StableId, row.DisplayName, ms, ms).ConfigureAwait(false);
                }
                catch { }
            }
            _log.Add(row.DisplayName, "Re-apply on connect", Input.MouseResult.AppliedNoVerify, "Re-applied saved Windows settings after the device arrived.");
        }
        LoadWindows();
    }

    private void CheckDrift()
    {
        try
        {
            foreach (var row in Devices)
            {
                if (!_prefs.Data.Applied.TryGetValue(row.Device.StableId, out var applied)) continue;
                var current = _windows.Read();
                foreach (var pair in applied)
                {
                    string now = pair.Key switch
                    {
                        "Pointer speed" => current.PointerSpeed.ToString(),
                        "Scroll lines" => current.WheelLines.ToString(),
                        "Double-click speed" => current.DoubleClickMs.ToString(),
                        _ => string.Empty,
                    };
                    if (now.Length > 0 && !string.Equals(now, pair.Value, StringComparison.OrdinalIgnoreCase))
                        _log.Add(row.DisplayName, pair.Key, Input.MouseResult.Drifted,
                            pair.Key + " changed after we set it: was " + pair.Value + ", now " + now + ".");
                }
            }
        }
        catch { }
    }

    public void StartTest()
    {
        var row = SelectedDevice;
        if (row is null || row.Device.Interfaces.Count == 0) return;
        ulong handle = row.Device.Interfaces[0].Handle;
        if (handle == 0) return;
        _lastResult = null;
        TestResultText = string.Empty;
        MismatchText = string.Empty;
        TestStatusText = "Move the mouse";
        if (_test.Start(handle, ConnectionText(row.Device.Connection)))
        {
            TestRunning = true;
            _roadSeconds = 0;
            _roadReports = 0;
            LiveSpeedText = string.Empty;
            TestStatusText = "Measuring. Move the mouse continuously in circles.";
        }
        else
        {
            TestStatusText = "Can't read: the test could not start for this mouse.";
        }
    }

    public void StopTest()
    {
        StopTestInternal();
    }

    private void StopTestInternal()
    {
        if (!_test.Running && !TestRunning) return;
        Input.ReportRateResult result;
        try
        {
            result = _test.Stop();
        }
        catch
        {
            TestRunning = false;
            return;
        }
        TestRunning = false;
        _lastResult = result;
        ReportMeasuredLine = "Not measured yet.";
        LiveSpeedText = string.Empty;
        _roadSeconds = 0;
        _roadReports = 0;
        if (result.DeviceWasRemoved)
        {
            TestStatusText = "The device was removed during the test.";
            TestResultText = string.Empty;
            return;
        }
        if (result.MovementReports == 0)
        {
            TestStatusText = "Move the mouse";
            TestResultText = "No movement was recorded.";
            return;
        }
        if (!result.HasEnoughData)
        {
            TestStatusText = "Move the mouse";
            TestResultText = "Not enough movement yet. Keep moving for at least 2 seconds.";
            return;
        }
        if (result.Unreliable)
        {
            TestStatusText = result.UnreliableReason + ".";
            TestResultText = string.Empty;
            return;
        }
        MismatchText = "Setting can't be read from this mouse.";
        TestStatusText = "Measured by this PC, approximate.";
        TestResultText = "Average " + Math.Round(result.AvgHz, 1) + " Hz"
            + ", min " + Math.Round(result.MinHz, 1) + " Hz"
            + ", max " + Math.Round(result.MaxHz, 1) + " Hz"
            + ". Median gap " + Math.Round(result.MedianIntervalMs, 3) + " ms"
            + ", 99th percentile " + Math.Round(result.P99IntervalMs, 3) + " ms"
            + ", " + result.OverTwiceMedian + " gaps over twice the median"
            + ". Connection: " + result.ConnectionText + ".";
        ReportMeasuredLine = "Measured by this PC: about " + Math.Round(result.AvgHz, 0) + " Hz.";
        if (SelectedDevice != null)
            SelectedDevice.MeasuredLine = "Measured about " + Math.Round(result.AvgHz, 0) + " Hz by this PC.";
    }

    public void AnalyzeDpi()
    {
        if (_lastResult is null || !_lastResult.HasEnoughData || _lastResult.Unreliable)
        {
            DpiResultText = "Run the report-rate test first.";
            return;
        }
        if (DpiInches <= 0)
        {
            DpiResultText = "Blocked by guard: the distance must be more than zero.";
            return;
        }
        double x = _lastResult.MoveX / DpiInches;
        double y = _lastResult.MoveY / DpiInches;
        DpiResultText = "Counts in the test: X " + _lastResult.MoveX + ", Y " + _lastResult.MoveY
            + ". That is about " + Math.Round(x, 1) + " by " + Math.Round(y, 1) + " counts per inch moved.";
    }

    public void RegisterClick()
    {
        double now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _clickTimes.Add(now);
        if (_clickTimes.Count > 8) _clickTimes.RemoveAt(0);
        if (_clickTimes.Count < 2)
        {
            ClickTestText = "Click again.";
            return;
        }
        double gap = _clickTimes[_clickTimes.Count - 1] - _clickTimes[_clickTimes.Count - 2];
        string verdict = gap <= DoubleClickMs ? "within" : "outside";
        ClickTestText = "Last gap " + Math.Round(gap, 0) + " ms, " + verdict + " the system double-click time of " + DoubleClickMs + " ms.";
    }

    private void OnTestProgress(Input.CaptureLive live)
    {
        _ui.TryEnqueue(() =>
        {
            if (!_testVisible) return;
            TestStatusText = "Measuring: " + live.Reports + " reports in " + Math.Round(live.Seconds, 1) + " s.";
            LiveButtonsText = ButtonsText(live.Buttons);
            LiveWheelText = "Wheel " + live.Wheel + ", horizontal " + live.HWheel + ".";
            double gapSeconds = live.Seconds - _roadSeconds;
            long gapReports = live.Reports - _roadReports;
            if (gapSeconds > 0 && gapReports >= 0)
            {
                double speed = gapReports / gapSeconds;
                LiveSpeedText = ((long)Math.Round(speed)) + " counts/sec";
                try { RoadUpdate?.Invoke(speed); }
                catch { }
            }
            _roadSeconds = live.Seconds;
            _roadReports = live.Reports;
        });
    }

    private void OnTestDeviceRemoved()
    {
        _ui.TryEnqueue(() =>
        {
            try { StopTestInternal(); } catch { }
            TestStatusText = "The device was removed during the test.";
        });
    }

    private static string ButtonsText(int mask)
    {
        var pressed = new List<string>();
        if ((mask & 1) != 0) pressed.Add("Left");
        if ((mask & 2) != 0) pressed.Add("Right");
        if ((mask & 4) != 0) pressed.Add("Middle");
        if ((mask & 8) != 0) pressed.Add("Button 4");
        if ((mask & 16) != 0) pressed.Add("Button 5");
        return pressed.Count == 0 ? "No buttons pressed." : string.Join(" + ", pressed) + " pressed.";
    }

    public async Task ApplyReportRateAsync(Microsoft.UI.Xaml.XamlRoot? root, string rate)
    {
        _log.Add(DeviceName(), "Report rate", Input.MouseResult.NotImplemented,
            "Not implemented in this app: no verified module can set the report rate on this mouse.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<bool> ConfirmKeepChangesAsync(Microsoft.UI.Xaml.XamlRoot? root, string what)
    {
        if (root is null) return false;
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Keep these changes?",
            Content = what + " The change reverts in 15 seconds unless kept.",
            PrimaryButtonText = "Keep",
            CloseButtonText = "Revert",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            XamlRoot = root,
        };
        var timer = new System.Threading.Timer(_ =>
        {
            _ui.TryEnqueue(() => { try { dialog.Hide(); } catch { } });
        }, null, 15000, System.Threading.Timeout.Infinite);
        try
        {
            var choice = await dialog.ShowAsync().AsTask().ConfigureAwait(false);
            return choice == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { timer.Dispose(); } catch { }
        }
    }
}
