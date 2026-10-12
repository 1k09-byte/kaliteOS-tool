// =============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>
/// Answers "which CPU is this on RIGHT NOW" for threads and devices.
///
/// Windows exposes no user-mode API for another thread's current processor
/// (GetCurrentProcessorNumberEx is the calling thread only; thread APIs return
/// affinity and ideal processor, never the running CPU), so this observes the
/// system instead: a real-time kernel ETW session (NT Kernel Logger) with
///   - CSwitch (flag 0x10, opcode 36): payload starts with NewThreadId, and the
///     event's buffer context carries the processor index - so every context
///     switch records "thread X ran on CPU N".
///   - DPC (flag 0x20, opcodes 66/68/69) and ISR (flag 0x40, opcode 67):
///     payload is {int64 InitialTime, pointer Routine} - the routine address is
///     mapped back to its kernel module (SystemModuleInformation), giving
///     "driver X handled interrupts on CPU N".
/// Device rows map to their function driver through
/// HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instanceId&gt;\Service, so a device
/// shows the CPU its interrupts actually land on - whether or not it is pinned.
///
/// The session costs real CPU at context-switch rates, so it is ref-counted:
/// AddRef while a live view is open, Release when it closes. Everything is
/// best-effort: if the kernel logger is taken or ETW is unavailable, the views
/// simply show "-" instead of numbers.
/// </summary>
public static class CpuObservationService
{
    private static readonly object Gate = new();
    private static int _refCount;
    private static bool _running;
    private static bool _broken;
    private static IntPtr _propsBuffer;
    private static ulong _sessionHandle;
    private static ulong _consumeHandle;
    private static System.Threading.Thread? _pump;
    private static readonly EventRecordCallback _callback = OnEvent; // must stay alive for the native side

    private sealed record Entry(int Cpu, long Ticks, long Count);
    private static readonly Dictionary<int, Entry> _threadCpu = new();
    private static readonly Dictionary<string, Entry> _driverCpu = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> _deviceDriver = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- session

    /// <summary>Starts observation if this is the first consumer.</summary>
    public static void AddRef()
    {
        lock (Gate)
        {
            _refCount++;
            if (_running || _broken || _refCount <= 0) return;
            _running = StartSession();
            if (!_running) _broken = true; // do not retry a failed kernel logger every tick
        }
    }

    /// <summary>Stops observation when the last consumer goes away.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            _refCount--;
            if (_refCount > 0) return;
            if (_running)
            {
                StopSession();
                _running = false;
            }
            _broken = false; // a later open may retry (e.g. the conflicting logger ended)
        }
    }

    /// <summary>True while the kernel session is delivering events.</summary>
    public static bool IsObserving { get { lock (Gate) return _running; } }

    private static bool StartSession()
    {
        try
        {
            BuildModuleMap();

            int size = Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 2 * 2048;
            _propsBuffer = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(_propsBuffer + i, 0);

            var props = new CpuObsNative.EVENT_TRACE_PROPERTIES
            {
                Wnode =
                {
                    BufferSize = (uint)size,
                    Guid = CpuObsNative.SystemTraceControlGuid,
                    ClientContext = 1,
                    Flags = CpuObsNative.WNODE_FLAG_TRACED_GUID
                },
                BufferSize = 64,
                MinimumBuffers = 64,
                MaximumBuffers = 128,
                FlushTimer = 1,
                LogFileMode = CpuObsNative.EVENT_TRACE_REAL_TIME_MODE,
                EnableFlags = CpuObsNative.EVENT_TRACE_FLAG_CSWITCH
                              | CpuObsNative.EVENT_TRACE_FLAG_DPC
                              | CpuObsNative.EVENT_TRACE_FLAG_INTERRUPT,
                LogFileNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>(),
                LoggerNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 2048
            };
            Marshal.StructureToPtr(props, _propsBuffer, false);

            uint err = CpuObsNative.StartTraceW(out _sessionHandle, CpuObsNative.KernelLoggerName, _propsBuffer);
            if (err == 183) // ERROR_ALREADY_EXISTS: someone owns the NT Kernel Logger.
            {
                // A previous run of THIS app can leave an orphaned real-time session
                // behind (started, then the consumer failed - it never gets stopped),
                // which makes every later start fail with 183 forever. Stop the
                // existing session by name and retry once.
                Log("StartTraceW returned 183 (kernel logger already owned); stopping the existing session and retrying once.");
                err = StopKernelSessionByName();
                if (err == 0)
                    err = CpuObsNative.StartTraceW(out _sessionHandle, CpuObsNative.KernelLoggerName, _propsBuffer);
            }
            if (err != 0)
            {
                Log($"StartTraceW failed: {err}{DescribeTraceError(err)}");
                CleanupFailedStart();
                return false;
            }

            var logfile = new CpuObsNative.EVENT_TRACE_LOGFILEW
            {
                LoggerName = Marshal.StringToHGlobalUni(CpuObsNative.KernelLoggerName),
                ProcessTraceMode = CpuObsNative.PROCESS_TRACE_MODE_REAL_TIME | CpuObsNative.PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            IntPtr logfilePtr = Marshal.AllocHGlobal(Marshal.SizeOf<CpuObsNative.EVENT_TRACE_LOGFILEW>());
            Marshal.StructureToPtr(logfile, logfilePtr, false);
            _consumeHandle = CpuObsNative.OpenTraceW(logfilePtr);
            Marshal.FreeHGlobal(logfilePtr);
            if (_consumeHandle == CpuObsNative.INVALID_PROCESSTRACE_HANDLE)
            {
                Log("OpenTraceW returned INVALID_PROCESSTRACE_HANDLE; stopping the just-started session so it cannot orphan the kernel logger.");
                CleanupFailedStart();
                return false;
            }
            Log($"Kernel session started (handle 0x{_sessionHandle:X}, consume handle 0x{_consumeHandle:X}).");

            _pump = new System.Threading.Thread(() =>
            {
                try
                {
                    ulong h = _consumeHandle;
                    CpuObsNative.ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero);
                }
                catch { /* session torn down */ }
            })
            { IsBackground = true, Name = "kaliteConfig cpu-obs" };
            _pump.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log($"StartSession threw: {ex.GetType().Name}: {ex.Message}");
            try { CleanupFailedStart(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Frees the properties buffer and, if a session was already created,
    /// stops it. A session left running without a consumer keeps the single
    /// NT Kernel Logger slot occupied, so every future start would fail.
    /// </summary>
    private static void CleanupFailedStart()
    {
        try
        {
            if (_sessionHandle != 0 || _propsBuffer != IntPtr.Zero) StopSession();
        }
        catch { }
    }

    /// <summary>Stops the NT Kernel Logger session by name; returns 0 on success.</summary>
    private static uint StopKernelSessionByName()
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            int size = Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 2 * 2048;
            buf = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(buf + i, 0);
            var props = new CpuObsNative.EVENT_TRACE_PROPERTIES
            {
                Wnode = { BufferSize = (uint)size },
                LogFileNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>(),
                LoggerNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 2048
            };
            Marshal.StructureToPtr(props, buf, false);
            uint err = CpuObsNative.ControlTraceW(0, CpuObsNative.KernelLoggerName, buf, CpuObsNative.EVENT_TRACE_CONTROL_STOP);
            Log($"ControlTraceW(STOP) on existing kernel session: {err}");
            return err;
        }
        catch (Exception ex)
        {
            Log($"StopKernelSessionByName threw: {ex.Message}");
            return 0xFFFFFFFF;
        }
        finally
        {
            try { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); } catch { }
        }
    }

    private static string DescribeTraceError(uint err) => err switch
    {
        5 => " (ERROR_ACCESS_DENIED - administrator rights required)",
        183 => " (ERROR_ALREADY_EXISTS)",
        6 => " (ERROR_INVALID_HANDLE)",
        87 => " (ERROR_INVALID_PARAMETER)",
        _ => ""
    };

    /// <summary>Best-effort diagnostics to %LOCALAPPDATA%\kaliteConfig\cpu-obs.log.</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kaliteConfig");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "cpu-obs.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never break observation */ }
    }

    private static void StopSession()
    {
        try
        {
            if (_consumeHandle != 0 && _consumeHandle != CpuObsNative.INVALID_PROCESSTRACE_HANDLE)
                CpuObsNative.CloseTrace(_consumeHandle);
            if (_sessionHandle != 0)
            {
                IntPtr props = _propsBuffer;
                if (props != IntPtr.Zero)
                {
                    for (int i = 0; i < Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 4096; i += 4)
                        Marshal.WriteInt32(props + i, 0);
                    var p = new CpuObsNative.EVENT_TRACE_PROPERTIES
                    {
                        Wnode = { BufferSize = (uint)(Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 4096) },
                        LogFileNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>(),
                        LoggerNameOffset = (uint)Marshal.SizeOf<CpuObsNative.EVENT_TRACE_PROPERTIES>() + 2048
                    };
                    Marshal.StructureToPtr(p, props, false);
                    CpuObsNative.ControlTraceW(_sessionHandle, null, props, CpuObsNative.EVENT_TRACE_CONTROL_STOP);
                }
            }
        }
        catch { }
        try { if (_propsBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_propsBuffer); } catch { }
        _propsBuffer = IntPtr.Zero;
        _sessionHandle = 0;
        _consumeHandle = 0;
    }

    // ---------------------------------------------------------------- events

    private static void OnEvent(IntPtr ev)
    {
        try
        {
            byte opcode = Marshal.ReadByte(ev + 45);                 // EVENT_HEADER.EventDescriptor.Opcode
            int cpu = Marshal.ReadInt16(ev + 80) & 0xFFFF;           // ETW_BUFFER_CONTEXT.ProcessorIndex
            IntPtr userData = Marshal.ReadIntPtr(ev + 96);           // EVENT_RECORD.UserData
            if (userData == IntPtr.Zero) return;
            long now = Environment.TickCount64;

            if (opcode == 36) // CSwitch: NewThreadId (uint32) at offset 0
            {
                int tid = Marshal.ReadInt32(userData);
                if (tid > 0)
                {
                    lock (Gate)
                    {
                        _threadCpu[tid] = new Entry(cpu, now, _threadCpu.TryGetValue(tid, out var e) ? e.Count + 1 : 1);
                        if (_threadCpu.Count > 60000) _threadCpu.Clear(); // pathological churn safety
                    }
                }
            }
            else if (opcode == 67 || opcode == 66 || opcode == 68 || opcode == 69) // ISR / ThreadDPC / DPC / TimerDPC
            {
                long routine = Marshal.ReadInt64(userData + 8);      // {int64 InitialTime, pointer Routine}
                string? mod = ModuleFor(routine);
                if (mod != null)
                {
                    lock (Gate)
                    {
                        _driverCpu[mod] = new Entry(cpu, now, _driverCpu.TryGetValue(mod, out var e) ? e.Count + 1 : 1);
                        if (_driverCpu.Count > 4096) _driverCpu.Clear();
                    }
                }
            }
        }
        catch { /* a malformed event must never kill the pump */ }
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Last CPU this thread was seen running on, and how long ago.</summary>
    public static bool TryGetThreadCpu(int tid, out int cpu, out long ageMs, out long events)
    {
        lock (Gate)
        {
            if (_threadCpu.TryGetValue(tid, out var e))
            {
                cpu = e.Cpu; ageMs = Environment.TickCount64 - e.Ticks; events = e.Count;
                return true;
            }
        }
        cpu = -1; ageMs = 0; events = 0;
        return false;
    }

    /// <summary>Last CPU this device's function driver handled DPC/ISRs on.</summary>
    public static bool TryGetDeviceCpu(string deviceInstanceId, out int cpu, out long ageMs, out long events)
    {
        string? module = DriverModuleForDevice(deviceInstanceId);
        if (module != null)
        {
            lock (Gate)
            {
                if (_driverCpu.TryGetValue(module, out var e))
                {
                    cpu = e.Cpu; ageMs = Environment.TickCount64 - e.Ticks; events = e.Count;
                    return true;
                }
            }
        }
        cpu = -1; ageMs = 0; events = 0;
        return false;
    }

    /// <summary>"5 · 0.2s ago" style text for thread rows; "-" when unknown.</summary>
    public static string DescribeThreadCpu(int tid)
    {
        if (!IsObserving) return "-";
        return TryGetThreadCpu(tid, out int cpu, out long ageMs, out _)
            ? $"{cpu} · {FormatAge(ageMs)}"
            : "-";
    }

    /// <summary>"3 · live" style text for device rows; "-" when unknown.</summary>
    public static string DescribeDeviceCpu(string deviceInstanceId)
    {
        if (!IsObserving) return "-";
        return TryGetDeviceCpu(deviceInstanceId, out int cpu, out long ageMs, out long events)
            ? $"{cpu} · {events} irq · {FormatAge(ageMs)}"
            : "-";
    }

    private static string FormatAge(long ageMs) => ageMs < 1000 ? "just now"
        : ageMs < 60_000 ? $"{ageMs / 1000.0:0.#}s ago"
        : $"{ageMs / 60_000.0:0.#}m ago";

    // ------------------------------------------------------- address → module

    private sealed record ModuleInfo(long Base, long Size, string Name);
    private static ModuleInfo[] _modules = Array.Empty<ModuleInfo>();

    private static void BuildModuleMap()
    {
        var list = new List<ModuleInfo>();
        try
        {
            int needed = 0;
            CpuObsNative.NtQuerySystemInformation(11, IntPtr.Zero, 0, out needed); // SystemModuleInformation
            if (needed <= 0) needed = 1 << 20;
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try
            {
                int status = CpuObsNative.NtQuerySystemInformation(11, buf, needed, out _);
                if (status != 0) return;
                int count = Marshal.ReadInt32(buf);
                int offset = 8; // RTL_PROCESS_MODULES: ULONG NumberOfModules + pad
                const int stride = 296; // RTL_PROCESS_MODULE_INFORMATION on x64
                for (int i = 0; i < count && i < 4096; i++)
                {
                    long imageBase = Marshal.ReadInt64(buf + offset + 16);
                    int imageSize = Marshal.ReadInt32(buf + offset + 24);
                    int nameOffset = Marshal.ReadInt16(buf + offset + 38) & 0xFFFF;
                    IntPtr namePtr = buf + offset + 40 + nameOffset;
                    string name = Marshal.PtrToStringAnsi(namePtr) ?? "";
                    if (name.Length > 0) list.Add(new ModuleInfo(imageBase, imageSize, name));
                    offset += stride;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        _modules = list.ToArray();
    }

    private static string? ModuleFor(long address)
    {
        var mods = _modules;
        foreach (var m in mods)
        {
            if (address >= m.Base && address < m.Base + m.Size) return m.Name;
        }
        return null;
    }

    /// <summary>Device instance id → its function driver's module name (e.g. "nvlddmkm.sys").</summary>
    private static string? DriverModuleForDevice(string deviceInstanceId)
    {
        lock (Gate)
        {
            if (_deviceDriver.TryGetValue(deviceInstanceId, out string? cached)) return cached;
        }
        string? service = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceInstanceId}");
            service = key?.GetValue("Service") as string;
        }
        catch { }
        string? module = string.IsNullOrWhiteSpace(service) ? null : service.Trim().ToLowerInvariant() + ".sys";
        lock (Gate) _deviceDriver[deviceInstanceId] = module;
        return module;
    }

    // ---------------------------------------------------------------- interop

    private delegate void EventRecordCallback(IntPtr eventRecord);

    private static class CpuObsNative
    {
        public const string KernelLoggerName = "NT Kernel Logger";
        public static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
        public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        public const uint EVENT_TRACE_FLAG_CSWITCH = 0x00000010;
        public const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
        public const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
        public const uint EVENT_TRACE_CONTROL_STOP = 1;
        public const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_TRACE_HEADER
        {
            public ushort Size;
            public ushort FieldTypeFlags;
            public uint Version;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public ulong ProcessorTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_TRACE
        {
            public EVENT_TRACE_HEADER Header;
            public uint InstanceId;
            public uint ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength;
            public uint ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TIME_ZONE_INFORMATION
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SYSTEMTIME StandardDate;
            public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SYSTEMTIME DaylightDate;
            public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize;
            public uint Version;
            public uint ProviderVersion;
            public uint NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName;
            public IntPtr LogFileName;
            public TIME_ZONE_INFORMATION TimeZone;
            public long BootTime;
            public long PerfFreq;
            public long StartTime;
            public uint ReservedFlags;
            public uint BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_TRACE_LOGFILEW
        {
            public IntPtr LogFileName;
            public IntPtr LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            public IntPtr EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint StartTraceW(out ulong TraceHandle, string InstanceName, IntPtr Properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint ControlTraceW(ulong TraceHandle, string? InstanceName, IntPtr Properties, uint ControlCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW")]
        public static extern ulong OpenTraceW(IntPtr logfile);

        [DllImport("advapi32.dll")]
        public static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        public static extern uint CloseTrace(ulong traceHandle);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
    }
}
