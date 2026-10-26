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
    public int MatchedReports;
    public int UnmatchedReports;
    public bool FallbackActive;
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
    public double ThreadCpuPercent;
    public double SystemCpuPercent;
    public string ConnectionText = string.Empty;
    public long LeftDownCount;
    public double LastLeftIntervalMs;
    public long WheelTotal;
    public long HWheelTotal;
}

public sealed class CaptureLive
{
    public int Reports;
    public int MatchedReports;
    public int UnmatchedReports;
    public bool FallbackActive;
    public double Seconds;
    public int Buttons;
    public long Wheel;
    public long HWheel;
    public long MoveX;
    public long MoveY;
    public double ThreadCpuPercent;
    public double SystemCpuPercent;
    public double WindowHz;
    public double SinceLastMs;
    public double WindowMedianMs;
    public double WindowP99Ms;
    public int WindowOverTwice;
    public int WindowSamples;
}

public interface IMouseTestService
{
    bool Running { get; }
    event Action<CaptureLive>? Progress;
    event Action? DeviceRemoved;
    bool Start(ulong deviceHandle, string connectionText);
    ReportRateResult Stop();
    void Abort();
    bool TakeMotion(out int dx, out int dy);
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
    private long _moveX;
    private long _moveY;
    private long _cpuThreadStart;
    private long _cpuIdleStart;
    private long _cpuKernelStart;
    private long _cpuUserStart;
    private double _cpuThreadPercent;
    private double _cpuSystemPercent;
    private int _liveButtons;
    private int _motionDx;
    private int _motionDy;
    private ulong _target;
    private string _connection = string.Empty;
    private bool _deviceGone;
    private long _qpcFrequency;
    private long _lastProgressQpc;
    private IntPtr _captureHwnd;
    private IntPtr _rawBuffer;
    private long _startQpc;
    private long _matchedMovement;
    private long _unmatchedMovement;
    private bool _fallback;
    private static readonly NativeInput.WndProc ClassProc = StaticWndProc;
    private static IntPtr _classProcPointer;
    private static MouseTestService? _activeService;
    private static long _registeredHwnd;
    private readonly IMouseLog? _log;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _readyOk;
    private int _loggedReports;

    public MouseTestService(IMouseLog log)
    {
        _log = log;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var service = _activeService;
            if (service != null) return service.WndProc(hwnd, message, wParam, lParam);
        }
        catch { }
        return NativeInput.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    public bool Running
    {
        get { lock (_gate) return _thread != null && _thread.IsAlive; }
    }

    public event Action<CaptureLive>? Progress;
    public event Action? DeviceRemoved;

    public bool TakeMotion(out int dx, out int dy)
    {
        dx = Interlocked.Exchange(ref _motionDx, 0);
        dy = Interlocked.Exchange(ref _motionDy, 0);
        return dx != 0 || dy != 0;
    }

    public bool Start(ulong deviceHandle, string connectionText)
    {
        lock (_gate)
        {
            if (_thread != null && _thread.IsAlive) return false;
            _samples.Clear();
            _leftDowns.Clear();
            _wheelTotal = 0;
            _hwheelTotal = 0;
            _moveX = 0;
            _moveY = 0;
            Interlocked.Exchange(ref _motionDx, 0);
            Interlocked.Exchange(ref _motionDy, 0);
            _stopRequested = false;
            _deviceGone = false;
            _lastProgressQpc = 0;
            _target = deviceHandle;
            _connection = connectionText ?? string.Empty;
            _startQpc = 0;
            _matchedMovement = 0;
            _unmatchedMovement = 0;
            _fallback = false;
            _loggedReports = 0;
            _readyOk = false;
            _ready.Reset();
            NativeInput.QueryPerformanceFrequency(out _qpcFrequency);
            if (_qpcFrequency <= 0) return false;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "MouseReportCapture",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        bool ready = false;
        try { ready = _ready.Wait(1500) && _readyOk; } catch { }
        if (!ready)
        {
            _stopRequested = true;
            WakeCapture();
            Thread? failed;
            lock (_gate) { failed = _thread; _thread = null; }
            try { failed?.Join(1000); } catch { }
        }
        return ready;
    }

    public ReportRateResult Stop()
    {
        _stopRequested = true;
        WakeCapture();
        Thread? thread;
        lock (_gate) thread = _thread;
        try { thread?.Join(6000); }
        catch { }
        bool alive = thread != null && thread.IsAlive;
        lock (_gate) { if (!alive) _thread = null; }
        _log?.Add("Mouse test", "Capture stopped", MouseResult.CantRead,
            "Matched " + Interlocked.Read(ref _matchedMovement) + " movement reports, unmatched "
            + Interlocked.Read(ref _unmatchedMovement) + ", recorded " + _samples.Count + ".");
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
            _activeService = this;
            ushort atom = RegisterCaptureClass();
            if (atom == 0)
            {
                Report("The capture window class could not be registered (" + Marshal.GetLastWin32Error() + ").");
                return;
            }
            IntPtr instance = NativeInput.GetModuleHandleW(null);
            hwnd = NativeInput.CreateWindowExW(0, "KaliteMouseCapture", string.Empty, 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Report("The capture window could not be created (" + Marshal.GetLastWin32Error() + ").");
                return;
            }
            _captureHwnd = hwnd;
            _cpuThreadStart = CurrentThreadCpu();
            NativeInput.GetSystemTimes(out var idleStart, out var kernelStart, out var userStart);
            _cpuIdleStart = ToTicks(idleStart);
            _cpuKernelStart = ToTicks(kernelStart);
            _cpuUserStart = ToTicks(userStart);
            var devices = new NativeInput.RawInputDevice[1];
            devices[0] = new NativeInput.RawInputDevice
            {
                UsagePage = NativeInput.HidUsagePageGeneric,
                Usage = NativeInput.HidUsageGenericMouse,
                Flags = NativeInput.RidevInputSink,
                Target = hwnd,
            };
            if (!NativeInput.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeInput.RawInputDevice>()))
            {
                Report("Raw Input could not be registered for the capture window (" + Marshal.GetLastWin32Error() + ").");
                return;
            }
            _registeredHwnd = hwnd.ToInt64();
            _readyOk = true;
            _ready.Set();
            while (!_stopRequested)
            {
                if (!NativeInput.PeekMessageW(out NativeInput.Msg msg, hwnd, 0, 0, 1))
                {
                    NativeInput.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, NativeInput.QsAllInput, 0);
                    continue;
                }
                NativeInput.TranslateMessage(ref msg);
                NativeInput.DispatchMessageW(ref msg);
                if (_stopRequested) break;
            }
            try
            {
                if (hwnd.ToInt64() == System.Threading.Interlocked.Read(ref _registeredHwnd))
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
                    System.Threading.Interlocked.Exchange(ref _registeredHwnd, 0);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Report("Capture stopped unexpectedly: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            _ready.Set();
            _activeService = null;
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
            if (_classProcPointer == IntPtr.Zero)
                _classProcPointer = Marshal.GetFunctionPointerForDelegate(ClassProc);
            IntPtr instance = NativeInput.GetModuleHandleW(null);
            var cls = new NativeInput.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeInput.WndClassEx>(),
                Style = 0,
                Proc = _classProcPointer,
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
                DrainMessage(lParam);
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

    private void DrainMessage(IntPtr rawInput)
    {
        try
        {
            uint header = (uint)Marshal.SizeOf<NativeInput.RawInputHeader>();
            uint size = 0;
            NativeInput.GetRawInputData(rawInput, NativeInput.RidInput, IntPtr.Zero, ref size, header);
            if (size == 0 || size > 256) return;
            if (_rawBuffer == IntPtr.Zero) _rawBuffer = Marshal.AllocHGlobal(256);
            uint got = NativeInput.GetRawInputData(rawInput, NativeInput.RidInput, _rawBuffer, ref size, header);
            if (got == unchecked((uint)-1) || got == 0) return;
            uint type = (uint)Marshal.ReadInt32(_rawBuffer, 0);
            if (type != NativeInput.RimTypeMouse) return;
            ulong handle = (ulong)Marshal.ReadInt64(_rawBuffer, 8);
            long stamp = NowQpc();
            if (_startQpc == 0) _startQpc = stamp;
            ushort flags = (ushort)Marshal.ReadInt16(_rawBuffer, 24);
            bool absolute = (flags & 0x01) != 0;
            bool moved = !absolute
                && (Marshal.ReadInt32(_rawBuffer, 36) != 0 || Marshal.ReadInt32(_rawBuffer, 40) != 0);
            if (Interlocked.Exchange(ref _loggedReports, 1) == 0)
                _log?.Add("Mouse test", "Capture started", MouseResult.CantRead,
                    "First report seen. Handle " + handle + ", target " + _target + ".");            if (handle == _target)
            {
                if (moved) _matchedMovement++;
            }
            else
            {
                if (moved) _unmatchedMovement++;
                bool waiting = _qpcFrequency > 0 && (stamp - _startQpc) > _qpcFrequency * 2;
                if (!waiting || _matchedMovement > 0 || _unmatchedMovement < 5) return;
                _fallback = true;
            }
            RecordMouse(_rawBuffer, stamp);
        }
        catch { }
    }

    private void Report(string message)
    {
        try { _log?.Add("Mouse test", "Capture", MouseResult.CantRead, message); } catch { }
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
        _moveX += dx;
        _moveY += dy;
        if (dx != 0) Interlocked.Add(ref _motionDx, dx);
        if (dy != 0) Interlocked.Add(ref _motionDy, dy);
        bool moved = dx != 0 || dy != 0;
        if (!moved && wheel == 0 && hwheel == 0 && (buttonFlags & 0x03FF) == 0) return;
        if (!moved) return;
        int total;
        lock (_gate)
        {
            _samples.Add(new ReportSample { Qpc = stamp, Dx = dx, Dy = dy, Buttons = buttons, Wheel = wheel, HWheel = hwheel });
            total = _samples.Count;
            if (total > 400_000)
            {
                _samples.RemoveRange(0, 200_000);
                total = _samples.Count;
            }
        }
        long gapTicks = stamp - _lastProgressQpc;
        if (_lastProgressQpc != 0 && gapTicks < _qpcFrequency / 10) return;
        _lastProgressQpc = stamp;
        double elapsed = _qpcFrequency > 0 ? (stamp - FirstStamp()) / (double)_qpcFrequency : 0;
        SampleCpu(elapsed);
        var live = new CaptureLive
        {
            Reports = total,
            MatchedReports = (int)_matchedMovement,
            UnmatchedReports = (int)_unmatchedMovement,
            FallbackActive = _fallback,
            Seconds = elapsed,
            Buttons = _liveButtons,
            Wheel = _wheelTotal,
            HWheel = _hwheelTotal,
            MoveX = _moveX,
            MoveY = _moveY,
            ThreadCpuPercent = _cpuThreadPercent,
            SystemCpuPercent = _cpuSystemPercent,
        };
        FillWindow(live);
        try { Progress?.Invoke(live); } catch { }
    }

    private static long ToTicks(NativeInput.FileTimeValue value)
        => ((long)value.High << 32) | value.Low;

    private static long CurrentThreadCpu()
    {
        try
        {
            if (!NativeInput.GetThreadTimes(NativeInput.GetCurrentThread(), out _, out _, out var kernel, out var user))
                return 0;
            return ToTicks(kernel) + ToTicks(user);
        }
        catch
        {
            return 0;
        }
    }

    private void SampleCpu(double seconds)
    {
        try
        {
            if (seconds <= 0) return;
            long used = CurrentThreadCpu() - _cpuThreadStart;
            if (used > 0)
                _cpuThreadPercent = Math.Clamp(used / 10_000_000.0 / seconds * 100.0, 0, 100);
            if (NativeInput.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long busy = (ToTicks(kernel) - _cpuKernelStart) + (ToTicks(user) - _cpuUserStart);
                long idleDelta = ToTicks(idle) - _cpuIdleStart;
                if (busy > 0)
                    _cpuSystemPercent = Math.Clamp((busy - idleDelta) / (double)busy * 100.0, 0, 100);
            }
        }
        catch
        {
        }
    }

    private void FillWindow(CaptureLive live)
    {
        if (_qpcFrequency <= 0) return;
        List<ReportSample> samples;
        long lastQpc;
        lock (_gate)
        {
            int count = _samples.Count;
            if (count == 0) return;
            lastQpc = _samples[count - 1].Qpc;
            long cutoff = lastQpc - _qpcFrequency;
            int start = count - 1;
            while (start > 0 && _samples[start - 1].Qpc >= cutoff) start--;
            int room = count - start;
            if (room > 4000) start = count - 4000;
            samples = _samples.GetRange(start, count - start);
        }
        if (samples.Count < 50) return;
        long first = samples[0].Qpc;
        double freq = _qpcFrequency;
        double span = (samples[samples.Count - 1].Qpc - first) / freq;
        if (span <= 0) return;
        var gaps = new List<double>(samples.Count);
        double previous = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double time = (samples[i].Qpc - first) / freq;
            if (i > 0 && time > previous) gaps.Add((time - previous) * 1000.0);
            previous = time;
        }
        if (gaps.Count < 40) return;
        live.SinceLastMs = (NowQpc() - samples[samples.Count - 1].Qpc) * 1000.0 / freq;
        double activeSum = 0;
        int activeCount = 0;
        var activeGaps = new List<double>(gaps.Count);
        foreach (double gap in gaps)
        {
            if (gap <= 0 || gap > 100) continue;
            activeGaps.Add(gap);
            activeSum += gap;
            activeCount++;
        }
        if (activeCount < 20 || activeSum <= 0) return;
        gaps = activeGaps;
        gaps.Sort();
        live.WindowSamples = samples.Count;
        live.WindowHz = activeCount / (activeSum / 1000.0);
        live.WindowMedianMs = gaps[gaps.Count / 2];
        int p99 = Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.99));
        live.WindowP99Ms = gaps[p99];
        int over = 0;
        foreach (double gap in gaps)
            if (live.WindowMedianMs > 0 && gap > live.WindowMedianMs * 2.0) over++;
        live.WindowOverTwice = over;
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
        result.ThreadCpuPercent = _cpuThreadPercent;
        result.SystemCpuPercent = _cpuSystemPercent;
        result.MatchedReports = (int)_matchedMovement;
        result.UnmatchedReports = (int)_unmatchedMovement;
        result.FallbackActive = _fallback;
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
        int tail = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double end = times[i] + 1.0;
            if (end > times[samples.Count - 1]) break;
            if (tail < i) tail = i;
            while (tail < samples.Count && times[tail] < end) tail++;
            int count = tail - i;
            if (count > 0) windows.Add(count);
        }
        if (windows.Count > 0)
        {
            windows.Sort();
            result.MinHz = windows[0];
            result.MaxHz = windows[windows.Count - 1];
        }
        var gaps = new List<double>();
        int zeroGaps = 0;
        int activeCount = 0;
        double activeSum = 0;
        for (int i = 1; i < times.Length; i++)
        {
            double gap = (times[i] - times[i - 1]) * 1000.0;
            if (gap <= 0) { zeroGaps++; continue; }
            if (gap > 100) continue;
            gaps.Add(gap);
            activeCount++;
            activeSum += gap;
        }
        if (activeCount < 100 || activeSum <= 0) return result;
        result.AvgHz = activeCount / (activeSum / 1000.0);
        gaps.Sort();
        result.MedianIntervalMs = gaps[gaps.Count / 2];
        int p99 = (int)(gaps.Count * 0.99);
        if (p99 >= gaps.Count) p99 = gaps.Count - 1;
        result.P99IntervalMs = gaps[p99];
        int over = 0;
        foreach (double gap in gaps)
            if (result.MedianIntervalMs > 0 && gap > result.MedianIntervalMs * 2.0) over++;
        result.OverTwiceMedian = over;
        double zeroFraction = (double)zeroGaps / Math.Max(1, times.Length - 1);
        if (zeroFraction > 0.3)
        {
            result.Unreliable = true;
            result.UnreliableReason = "This PC could not keep up, so the measurement is unreliable";
        }
        result.HasEnoughData = true;
        return result;
    }
}
