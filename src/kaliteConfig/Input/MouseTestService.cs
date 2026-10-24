using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using kaliteConfig.Input.Interop;

namespace kaliteConfig.Input;

public sealed class ReportSample
{
    public long Qpc;
    public int Dx;
    public int Dy;
    public uint Buttons;
    public int Wheel;
    public int HWheel;
}

public sealed class ReportRateResult
{
    public bool DeviceWasRemoved;
    public bool HasEnoughData;
    public bool Unreliable;
    public string UnreliableReason = string.Empty;
    public double AvgHz;
    public double MinHz;
    public double MaxHz;
    public double MedianIntervalMs;
    public double P99IntervalMs;
    public int OverTwiceMedian;
    public int MovementReports;
    public double TotalSeconds;
    public long MoveX;
    public long MoveY;
    public string ConnectionText = string.Empty;
    public long LeftDownCount;
    public double LastLeftIntervalMs;
    public long WheelTotal;
    public long HWheelTotal;
}

public sealed class CaptureLive
{
    public int Reports;
    public double Seconds;
    public int Buttons;
    public long Wheel;
    public long HWheel;
}

public interface IMouseTestService
{
    bool Running { get; }
    event Action<CaptureLive>? Progress;
    event Action? DeviceRemoved;
    bool Start(ulong deviceHandle, string connectionText);
    ReportRateResult Stop();
    void Abort();
}

public sealed class MouseTestService : IMouseTestService
{
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
    private readonly List<ReportSample> _samples = new();
    private readonly List<long> _leftDowns = new();
    private long _wheelTotal;
    private long _hwheelTotal;
    private int _liveButtons;
    private ulong _target;
    private string _connection = string.Empty;
    private bool _deviceGone;
    private long _qpcFrequency;
    private int _maxDrain;
    private int _lastProgressCount;
    private IntPtr _captureHwnd;
    private NativeInput.WndProc? _wndProc;
    private static readonly IntPtr HwndMessage = new(-3);

    public bool Running
    {
        get { lock (_gate) return _thread != null && _thread.IsAlive; }
    }

    public event Action<CaptureLive>? Progress;
    public event Action? DeviceRemoved;

    public bool Start(ulong deviceHandle, string connectionText)
    {
        lock (_gate)
        {
            if (_thread != null && _thread.IsAlive) return false;
            _samples.Clear();
            _leftDowns.Clear();
            _wheelTotal = 0;
            _hwheelTotal = 0;
            _stopRequested = false;
            _deviceGone = false;
            _maxDrain = 0;
            _lastProgressCount = 0;
            _target = deviceHandle;
            _connection = connectionText ?? string.Empty;
            NativeInput.QueryPerformanceFrequency(out _qpcFrequency);
            if (_qpcFrequency <= 0) return false;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "MouseReportCapture",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            return true;
        }
    }

    public ReportRateResult Stop()
    {
        _stopRequested = true;
        WakeCapture();
        Thread? thread;
        lock (_gate) thread = _thread;
        try { thread?.Join(3000); }
        catch { }
        lock (_gate) _thread = null;
        return BuildResult();
    }

    public void Abort()
    {
        _stopRequested = true;
        WakeCapture();
        Thread? thread;
        lock (_gate) thread = _thread;
        try { thread?.Join(1500); }
        catch { }
        lock (_gate) _thread = null;
    }

    private void WakeCapture()
    {
        try
        {
            IntPtr hwnd = _captureHwnd;
            if (hwnd != IntPtr.Zero) NativeInput.PostMessageW(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private void CaptureLoop()
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            _wndProc = WndProc;
            ushort atom = RegisterCaptureClass();
            if (atom == 0) return;
            IntPtr instance = NativeInput.GetModuleHandleW(null);
            hwnd = NativeInput.CreateWindowExW(0, "KaliteMouseCapture", string.Empty, 0,
                0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;
            _captureHwnd = hwnd;
            var devices = new NativeInput.RawInputDevice[1];
            devices[0] = new NativeInput.RawInputDevice
            {
                UsagePage = NativeInput.HidUsagePageGeneric,
                Usage = NativeInput.HidUsageGenericMouse,
                Flags = NativeInput.RidevInputSink,
                Target = hwnd,
            };
            if (!NativeInput.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeInput.RawInputDevice>()))
                return;
            while (!_stopRequested)
            {
                int got = NativeInput.GetMessageW(out NativeInput.Msg msg, hwnd, 0, 0);
                if (got == 0 || got == -1) break;
                NativeInput.TranslateMessage(ref msg);
                NativeInput.DispatchMessageW(ref msg);
                if (_stopRequested) break;
            }
            try
            {
                var off = new NativeInput.RawInputDevice[1];
                off[0] = new NativeInput.RawInputDevice
                {
                    UsagePage = NativeInput.HidUsagePageGeneric,
                    Usage = NativeInput.HidUsageGenericMouse,
                    Flags = NativeInput.RidevRemove,
                    Target = IntPtr.Zero,
                };
                NativeInput.RegisterRawInputDevices(off, 1, (uint)Marshal.SizeOf<NativeInput.RawInputDevice>());
            }
            catch { }
        }
        catch { }
        finally
        {
            _captureHwnd = IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                try { NativeInput.DestroyWindow(hwnd); }
                catch { }
            }
        }
    }

    private ushort RegisterCaptureClass()
    {
        try
        {
            IntPtr instance = NativeInput.GetModuleHandleW(null);
            var cls = new NativeInput.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeInput.WndClassEx>(),
                Style = 0,
                Proc = _wndProc ?? WndProc,
                ClsExtra = 0,
                WndExtra = 0,
                Instance = instance,
                Icon = IntPtr.Zero,
                Cursor = IntPtr.Zero,
                Background = IntPtr.Zero,
                MenuName = null,
                ClassName = "KaliteMouseCapture",
                IconSm = IntPtr.Zero,
            };
            ushort atom = NativeInput.RegisterClassExW(ref cls);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1410) return 1;
            }
            return atom;
        }
        catch
        {
            return 0;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == 0x00FF)
            {
                DrainInput();
                return NativeInput.DefWindowProcW(hwnd, message, wParam, lParam);
            }
            if (message == 0x00FE)
            {
                int change = wParam.ToInt32();
                if (change == 2 && ((ulong)lParam.ToInt64() == _target))
                {
                    _deviceGone = true;
                    _stopRequested = true;
                    try { DeviceRemoved?.Invoke(); } catch { }
                }
                return NativeInput.DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }
        catch { }
        return NativeInput.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void DrainInput()
    {
        try
        {
            uint size = 0;
            NativeInput.GetRawInputBuffer(IntPtr.Zero, ref size, 24);
            if (size == 0) return;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint count = NativeInput.GetRawInputBuffer(buffer, ref size, 24);
                if (count == unchecked((uint)-1) || count == 0) return;
                if (count > _maxDrain) _maxDrain = (int)count;
                long stamp = NowQpc();
                IntPtr current = buffer;
                for (uint i = 0; i < count; i++)
                {
                    uint entrySize = (uint)Marshal.ReadInt32(current, 4);
                    if (entrySize < 48) break;
                    uint type = (uint)Marshal.ReadInt32(current, 0);
                    if (type == NativeInput.RimTypeMouse)
                    {
                        ulong handle = (ulong)Marshal.ReadInt64(current, 8);
                        if (handle == _target) RecordMouse(current, stamp);
                    }
                    current = IntPtr.Add(current, checked((int)entrySize));
                    if (current.ToInt64() >= buffer.ToInt64() + size) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { }
    }

    private void RecordMouse(IntPtr entry, long stamp)
    {
        ushort flags = (ushort)Marshal.ReadInt16(entry, 24);
        bool absolute = (flags & 0x01) != 0;
        uint buttons = (uint)Marshal.ReadInt32(entry, 28);
        int wheel = 0;
        int hwheel = 0;
        ushort buttonFlags = (ushort)(buttons & 0xFFFF);
        short buttonData = (short)((buttons >> 16) & 0xFFFF);
        if ((buttonFlags & 0x0400) != 0) wheel = buttonData;
        if ((buttonFlags & 0x0800) != 0) hwheel = buttonData;
        int dx = absolute ? 0 : Marshal.ReadInt32(entry, 36);
        int dy = absolute ? 0 : Marshal.ReadInt32(entry, 40);
        if ((buttonFlags & 0x0001) != 0)
        {
            _leftDowns.Add(stamp);
            if (_leftDowns.Count > 64) _leftDowns.RemoveAt(0);
            _liveButtons |= 1;
        }
        if ((buttonFlags & 0x0002) != 0) _liveButtons &= ~1;
        if ((buttonFlags & 0x0004) != 0) _liveButtons |= 2;
        if ((buttonFlags & 0x0008) != 0) _liveButtons &= ~2;
        if ((buttonFlags & 0x0010) != 0) _liveButtons |= 4;
        if ((buttonFlags & 0x0020) != 0) _liveButtons &= ~4;
        if ((buttonFlags & 0x0040) != 0) _liveButtons |= 8;
        if ((buttonFlags & 0x0080) != 0) _liveButtons &= ~8;
        if ((buttonFlags & 0x0100) != 0) _liveButtons |= 16;
        if ((buttonFlags & 0x0200) != 0) _liveButtons &= ~16;
        _wheelTotal += wheel;
        _hwheelTotal += hwheel;
        bool moved = dx != 0 || dy != 0;
        if (!moved && wheel == 0 && hwheel == 0 && (buttonFlags & 0x03FF) == 0) return;
        if (!moved) return;
        lock (_gate) _samples.Add(new ReportSample { Qpc = stamp, Dx = dx, Dy = dy, Buttons = buttons, Wheel = wheel, HWheel = hwheel });
        int total;
        lock (_gate) total = _samples.Count;
        if (total - _lastProgressCount >= 25)
        {
            _lastProgressCount = total;
            long now = stamp;
            double seconds = _qpcFrequency > 0 ? (now - FirstStamp()) / (double)_qpcFrequency : 0;
            try { Progress?.Invoke(new CaptureLive { Reports = total, Seconds = seconds, Buttons = _liveButtons, Wheel = _wheelTotal, HWheel = _hwheelTotal }); } catch { }
        }
    }

    private long FirstStamp()
    {
        lock (_gate) return _samples.Count > 0 ? _samples[0].Qpc : NowQpc();
    }

    private long NowQpc()
    {
        NativeInput.QueryPerformanceCounter(out long value);
        return value;
    }

    private ReportRateResult BuildResult()
    {
        var result = new ReportRateResult { ConnectionText = _connection, DeviceWasRemoved = _deviceGone };
        List<ReportSample> samples;
        lock (_gate) samples = _samples.ToList();
        result.MovementReports = samples.Count;
        result.LeftDownCount = _leftDowns.Count;
        if (_leftDowns.Count >= 2 && _qpcFrequency > 0)
        {
            long last = _leftDowns[_leftDowns.Count - 1];
            long prev = _leftDowns[_leftDowns.Count - 2];
            result.LastLeftIntervalMs = (last - prev) * 1000.0 / _qpcFrequency;
        }
        result.WheelTotal = _wheelTotal;
        result.HWheelTotal = _hwheelTotal;
        long moveX = 0;
        long moveY = 0;
        foreach (var sample in samples)
        {
            moveX += Math.Abs((long)sample.Dx);
            moveY += Math.Abs((long)sample.Dy);
        }
        result.MoveX = moveX;
        result.MoveY = moveY;
        if (samples.Count < 2 || _qpcFrequency <= 0) return result;
        double freq = _qpcFrequency;
        long first = samples[0].Qpc;
        long lastStamp = samples[samples.Count - 1].Qpc;
        double seconds = (lastStamp - first) / freq;
        result.TotalSeconds = seconds;
        if (seconds < 2.0) return result;
        var times = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++) times[i] = (samples[i].Qpc - first) / freq;
        var windows = new List<double>();
        for (int i = 0; i < samples.Count; i++)
        {
            double end = times[i] + 1.0;
            if (end > times[samples.Count - 1]) break;
            int count = 0;
            for (int j = i; j < samples.Count && times[j] < end; j++) count++;
            if (count > 0) windows.Add(count);
        }
        if (windows.Count == 0) return result;
        windows.Sort();
        result.MinHz = windows[0];
        result.MaxHz = windows[windows.Count - 1];
        double sum = 0;
        foreach (double window in windows) sum += window;
        result.AvgHz = sum / windows.Count;
        var gaps = new List<double>();
        int zeroGaps = 0;
        for (int i = 1; i < times.Length; i++)
        {
            double gap = (times[i] - times[i - 1]) * 1000.0;
            if (gap <= 0) zeroGaps++;
            else gaps.Add(gap);
        }
        if (gaps.Count > 0)
        {
            gaps.Sort();
            result.MedianIntervalMs = gaps[gaps.Count / 2];
            int p99 = (int)(gaps.Count * 0.99);
            if (p99 >= gaps.Count) p99 = gaps.Count - 1;
            result.P99IntervalMs = gaps[p99];
            int over = 0;
            foreach (double gap in gaps)
                if (result.MedianIntervalMs > 0 && gap > result.MedianIntervalMs * 2.0) over++;
            result.OverTwiceMedian = over;
        }
        double zeroFraction = (double)zeroGaps / times.Length;
        if (_maxDrain > 128 || zeroFraction > 0.3)
        {
            result.Unreliable = true;
            result.UnreliableReason = "This PC could not keep up, so the measurement is unreliable";
        }
        result.HasEnoughData = true;
        return result;
    }
}
