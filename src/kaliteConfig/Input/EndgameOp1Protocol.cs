using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using kaliteConfig.Input.Interop;

namespace kaliteConfig.Input;

public sealed class EndgameDeviceStatus
{
    public int DpiX { get; set; }
    public int DpiY { get; set; }
    public int RateHz { get; set; }
    public int LodIndex { get; set; }
    public int LodCount { get; set; }
    public bool MotionSync { get; set; }
    public bool AngleSnapping { get; set; }
    public bool RippleControl { get; set; }
    public bool SlamclickFilter { get; set; }
    public bool JitterFilter { get; set; }
    public bool IsV2 { get; set; }
}

public sealed class EndgameOp1Protocol : IMouseProtocol
{
    public string Id => "endgame-gear-op1";
    public bool EnabledByDefault => false;

    public string LastError { get; private set; } = string.Empty;

    private const ushort VendorId = 0x3367;
    private const byte ReportConfig = 0xA0;
    private const byte ReportCommand = 0xA1;
    private const int ConfigSize = 1041;
    private const int CommandSize = 64;
    private const byte OpLoad = 0x12;
    private const byte OpStore = 0x11;
    private const byte StatusOk = 0x01;
    private const byte StatusBusy = 0x03;
    private const int OffDivider = 21;
    private const int OffFlags = 22;
    private const int OffLod = 25;
    private const int OffSnap = 26;
    private const int OffRipple = 27;
    private const int OffMotion = 28;
    private const int OffLevels = 30;
    private const int OffStages = 51;
    private const byte FlagSlam = 0x01;
    private const byte FlagJitter = 0x10;

    private static readonly ushort[] WiredPids = new ushort[] { 0x1964, 0x1966, 0x1976, 0x1978, 0x1980 };
    private static readonly ushort[] V2Pids = new ushort[] { 0x1976, 0x1978, 0x1980 };
    private static readonly int[] Rates = new int[] { 125, 250, 500, 1000, 2000, 4000, 8000 };

    public static bool IsAllowedId(ushort vid, ushort pid)
    {
        if (vid != VendorId) return false;
        foreach (ushort p in WiredPids)
            if (p == pid) return true;
        return false;
    }

    public static IReadOnlyList<int> SupportedRates() => Rates;

    public bool Matches(MouseDevice device) => IsAllowedId(device.VendorId, device.ProductId);

    public async Task<IReadOnlyList<MouseCapability>> ProbeAsync(MouseDevice device)
    {
        var list = new List<MouseCapability>();
        try
        {
            await ReadStatusAsync(device).ConfigureAwait(false);
        }
        catch
        {
            list.Add(new MouseCapability(CapabilityKind.ReportRate, CapabilityState.CantRead, "The mouse did not answer the probe."));
            return list;
        }
        list.Add(new MouseCapability(CapabilityKind.DeviceInfo, CapabilityState.Available, string.Empty));
        list.Add(new MouseCapability(CapabilityKind.WindowsSettings, CapabilityState.Available, "Applies to all mice."));
        list.Add(new MouseCapability(CapabilityKind.Sensor, CapabilityState.Available, "Verified on this device."));
        list.Add(new MouseCapability(CapabilityKind.ReportRate, CapabilityState.Available, "Verified on this device."));
        list.Add(new MouseCapability(CapabilityKind.Buttons, CapabilityState.NotImplemented, "Button remap is not implemented in this app."));
        list.Add(new MouseCapability(CapabilityKind.Scroll, CapabilityState.NotImplemented, "Scroll behavior is not implemented in this app."));
        list.Add(new MouseCapability(CapabilityKind.Lighting, CapabilityState.NotImplemented, "Lighting is not implemented in this app."));
        list.Add(new MouseCapability(CapabilityKind.Power, CapabilityState.NotImplemented, "Power controls are not implemented in this app."));
        list.Add(new MouseCapability(CapabilityKind.Profiles, CapabilityState.NotImplemented, "Onboard profiles are not implemented in this app."));
        list.Add(new MouseCapability(CapabilityKind.Test, CapabilityState.Available, string.Empty));
        list.Add(new MouseCapability(CapabilityKind.Log, CapabilityState.Available, string.Empty));
        return list;
    }

    public async Task<string?> ReadAsync(MouseDevice device, string setting)
    {
        EndgameDeviceStatus status;
        try
        {
            status = await ReadStatusAsync(device).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        switch ((setting ?? string.Empty).ToLowerInvariant())
        {
            case "report-rate": return status.RateHz.ToString();
            case "dpi": return status.DpiX.ToString();
            case "dpi-y": return status.DpiY.ToString();
            case "motion-sync": return status.MotionSync ? "On" : "Off";
            case "angle-snapping": return status.AngleSnapping ? "On" : "Off";
            case "ripple-control": return status.RippleControl ? "On" : "Off";
            case "slamclick-filter": return status.SlamclickFilter ? "On" : "Off";
            case "jitter-filter": return status.JitterFilter ? "On" : "Off";
            case "lift-off": return status.LodIndex.ToString();
            default: return null;
        }
    }

    public async Task<bool> WriteAsync(MouseDevice device, string setting, string value)
    {
        bool v2 = IsV2(device.ProductId);
        byte[] config;
        try
        {
            config = await LoadConfigAsync(device).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        switch ((setting ?? string.Empty).ToLowerInvariant())
        {
            case "report-rate":
                if (!int.TryParse(value, out int rate) || Array.IndexOf(Rates, rate) < 0) return false;
                config[OffDivider] = (byte)(8000 / rate);
                if (rate == 8000 && !v2) config[OffMotion] = 0;
                break;
            case "dpi":
                if (!int.TryParse(value, out int dpi) || !IsValidDpi(v2, dpi)) return false;
                WriteStages(config, dpi, dpi);
                break;
            case "motion-sync":
                bool motion = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase);
                if (motion && !v2 && DecodeRate(config[OffDivider]) == 8000) return false;
                config[OffMotion] = motion ? (byte)1 : (byte)0;
                break;
            case "angle-snapping":
                config[OffSnap] = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
                break;
            case "ripple-control":
                config[OffRipple] = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
                break;
            case "slamclick-filter":
                config[OffFlags] = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase)
                    ? (byte)(config[OffFlags] | FlagSlam)
                    : (byte)(config[OffFlags] & ~FlagSlam);
                break;
            case "jitter-filter":
                config[OffFlags] = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase)
                    ? (byte)(config[OffFlags] | FlagJitter)
                    : (byte)(config[OffFlags] & ~FlagJitter);
                break;
            case "lift-off":
                if (!int.TryParse(value, out int lod)) return false;
                if (lod < 0 || lod >= LodCount(v2)) return false;
                config[OffLod] = (byte)lod;
                break;
            default:
                return false;
        }
        try
        {
            return await StoreConfigAsync(device, config).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<EndgameDeviceStatus> ReadStatusAsync(MouseDevice device)
    {
        byte[] config = await LoadConfigAsync(device).ConfigureAwait(false);
        return ParseStatus(device.ProductId, config);
    }

    private static bool IsV2(ushort pid)
    {
        foreach (ushort p in V2Pids)
            if (p == pid) return true;
        return false;
    }

    private static int LodCount(bool v2) => v2 ? 11 : 3;

    private static bool IsValidDpi(bool v2, int dpi)
    {
        if (v2)
        {
            if (dpi < 10 || dpi > 30000) return false;
            int step = dpi <= 10000 ? 10 : 50;
            return dpi % step == 0;
        }
        return dpi >= 50 && dpi <= 26000 && dpi % 50 == 0;
    }

    private static int DecodeRate(byte divider) => divider == 0 ? 0 : 8000 / divider;

    private static int ReadU16(byte[] buffer, int offset) => buffer[offset] | (buffer[offset + 1] << 8);

    private static void WriteU16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteStages(byte[] config, int x, int y)
    {
        int levels = config[OffLevels];
        if (levels < 1) levels = 1;
        if (levels > 4) levels = 4;
        for (int level = 0; level < levels; level++)
        {
            int offset = OffStages + level * 5;
            config[offset] = x == y ? (byte)0 : (byte)1;
            WriteU16(config, offset + 1, x);
            WriteU16(config, offset + 3, y);
        }
    }

    private static EndgameDeviceStatus ParseStatus(ushort pid, byte[] config)
    {
        bool v2 = IsV2(pid);
        return new EndgameDeviceStatus
        {
            DpiX = ReadU16(config, OffStages + 1),
            DpiY = ReadU16(config, OffStages + 3),
            RateHz = DecodeRate(config[OffDivider]),
            LodIndex = config[OffLod],
            LodCount = LodCount(v2),
            MotionSync = config[OffMotion] != 0,
            AngleSnapping = config[OffSnap] != 0,
            RippleControl = config[OffRipple] != 0,
            SlamclickFilter = (config[OffFlags] & FlagSlam) != 0,
            JitterFilter = (config[OffFlags] & FlagJitter) != 0,
            IsV2 = v2,
        };
    }

    private static bool IsValidConfig(byte[] config, int length, ushort pid)
    {
        if (config is null || length < 131) return false;
        if (config[1] != StatusOk) return false;
        int levels = config[OffLevels];
        if (levels < 1 || levels > 4) return false;
        byte divider = config[OffDivider];
        if (divider < 1) return false;
        int dpi = ReadU16(config, OffStages + 1);
        return IsValidDpi(IsV2(pid), dpi);
    }

    private static List<string> VendorPaths(MouseDevice device)
    {
        var vendor = new List<string>();
        var other = new List<string>();
        foreach (var face in device.Interfaces)
        {
            if (face.DevicePath.Length == 0) continue;
            if (face.VendorDefined)
            {
                if (!vendor.Contains(face.DevicePath)) vendor.Add(face.DevicePath);
                continue;
            }
            if (!other.Contains(face.DevicePath)) other.Add(face.DevicePath);
        }
        var ordered = new List<string>();
        foreach (string path in vendor) if (!ordered.Contains(path)) ordered.Add(path);
        foreach (string path in other) if (!ordered.Contains(path)) ordered.Add(path);
        return ordered;
    }

    private async Task<byte[]> LoadConfigAsync(MouseDevice device)
    {
        var paths = VendorPaths(device);
        if (paths.Count == 0)
        {
            LastError = "The vendor interface was not found.";
            throw new IOException(LastError, unchecked((int)0x80070002));
        }
        Exception? last = null;
        foreach (string path in paths)
        {
            try
            {
                return await WithTimeout(() => AttemptRead(device.ProductId, path)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        LastError = last?.Message ?? "The mouse did not answer the probe.";
        throw last ?? new IOException(LastError, unchecked((int)0x8007001F));
    }

    private async Task<bool> StoreConfigAsync(MouseDevice device, byte[] config)
    {
        var paths = VendorPaths(device);
        if (paths.Count == 0) return false;
        foreach (string path in paths)
        {
            try
            {
                return await WithTimeout(() => AttemptStore(path, config)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        return false;
    }

    private static async Task<T> WithTimeout<T>(Func<T> work)
    {
        var task = Task.Run(work);
        var completed = await Task.WhenAny(task, Task.Delay(12000)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
            throw new TimeoutException("The mouse did not answer in time.");
        return await task.ConfigureAwait(false);
    }

    private static byte[] AttemptRead(ushort pid, string path)
    {
        IntPtr handle = OpenDevice(path);
        try
        {
            var command = new byte[CommandSize];
            command[0] = ReportCommand;
            command[1] = OpLoad;
            if (!SetReport(handle, command))
                throw new IOException("The command was not accepted.", Marshal.GetLastWin32Error());
            System.Threading.Thread.Sleep(80);
            for (int round = 0; round < 3; round++)
            {
                foreach (byte id in new byte[] { ReportCommand, ReportConfig })
                {
                    byte[]? got = GetReport(handle, id, ConfigSize);
                    if (got != null && IsValidConfig(got, got.Length, pid))
                    {
                        got[0] = ReportConfig;
                        return got;
                    }
                }
                System.Threading.Thread.Sleep(60 << round);
            }
            throw new IOException("The mouse did not return a valid configuration.", unchecked((int)0x8007001F));
        }
        finally
        {
            try { NativeInput.CloseHandle(handle); } catch { }
        }
    }

    private static bool AttemptStore(string path, byte[] config)
    {
        IntPtr handle = OpenDevice(path);
        try
        {
            var buffer = new byte[ConfigSize];
            buffer[0] = ReportConfig;
            buffer[1] = OpStore;
            Buffer.BlockCopy(config, 2, buffer, 2, ConfigSize - 2);
            if (!SetReport(handle, buffer))
                return false;
            System.Threading.Thread.Sleep(300);
            int backoff = 60;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[]? reply = GetReport(handle, ReportCommand, CommandSize);
                if (reply != null && reply.Length > 1)
                {
                    if (reply[1] == StatusOk) return true;
                    if (reply[1] != StatusBusy && attempt > 1) return false;
                }
                System.Threading.Thread.Sleep(backoff);
                if (backoff < 400) backoff *= 2;
            }
            return false;
        }
        finally
        {
            try { NativeInput.CloseHandle(handle); } catch { }
        }
    }

    private static readonly uint[] AccessModes = new uint[] { 0xC0000000, 0x80000000, 0 };

    private static IntPtr OpenDevice(string path)
    {
        const uint share = 3;
        const uint open = 3;
        int lastError = 0;
        foreach (uint access in AccessModes)
        {
            IntPtr handle = NativeInput.CreateFileW(path, access, share, IntPtr.Zero, open, 0, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) return handle;
            lastError = Marshal.GetLastWin32Error();
            if (lastError != 5) break;
        }
        throw new IOException("The vendor interface could not be opened (code " + lastError + ").", lastError);
    }

    private static bool SetReport(IntPtr handle, byte[] buffer)
    {
        IntPtr native = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, native, buffer.Length);
            return NativeInput.HidD_SetFeature(handle, native, (uint)buffer.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private static byte[]? GetReport(IntPtr handle, byte id, int size)
    {
        IntPtr native = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(native, i, 0);
            Marshal.WriteByte(native, 0, id);
            if (!NativeInput.HidD_GetFeature(handle, native, (uint)size))
                return null;
            var buffer = new byte[size];
            Marshal.Copy(native, buffer, 0, size);
            return buffer;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }
}
