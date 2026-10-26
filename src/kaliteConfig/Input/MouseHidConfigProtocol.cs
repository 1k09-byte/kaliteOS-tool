using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using kaliteConfig.Input.Interop;

namespace kaliteConfig.Input;

public sealed class MouseHidConfigProtocol : IMouseProtocol
{
    public string Id => "hid-config";
    public bool EnabledByDefault => false;
    public string LastError { get; private set; } = string.Empty;
    public static string Source => "Community-documented HID configuration protocol, OpenMouse mouse-protocol";

    private const ushort VendorId = 0x3367;
    private const byte ReportConfig = 0xA0;
    private const byte ReportCommand = 0xA1;
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
    private const int OffButtons = 77;
    private const byte SpdtSpeed = 0xF1;
    private const byte SpdtSafe = 0xF0;
    private const byte SpdtOff = 0x08;
    private const byte FlagSlam = 0x01;
    private const byte FlagJitter = 0x10;
    private const int MaxMulticlick = 25;
    private const int ButtonCount = 5;

    private static readonly ushort[] KnownPids = new ushort[] { 0x1964, 0x1966, 0x1976, 0x1978, 0x1980, 0x1990, 0x1991 };
    private static readonly ushort[] V2Pids = new ushort[] { 0x1976, 0x1978, 0x1980, 0x1990, 0x1991 };
    private static readonly int[] Rates = new int[] { 125, 250, 500, 1000, 2000, 4000, 8000 };
    private static readonly string[] SpdtNames = new string[] { "Off", "GX Speed", "GX Safe" };

    private string _livePath = string.Empty;
    private int _liveFeature;
    private int[] _liveRates = Array.Empty<int>();

    public static bool IsKnownVendor(ushort vid) => vid == VendorId;

    public static bool IsKnownId(ushort vid, ushort pid)
    {
        if (vid != VendorId) return false;
        foreach (ushort p in KnownPids)
            if (p == pid) return true;
        return false;
    }

    public IReadOnlyList<int> SupportedRates => _liveRates;

    private static bool RateKnown(int[] rates, int rate) => Array.IndexOf(rates, rate) >= 0;

    public bool Matches(MouseDevice device)
    {
        if (device is null || device.IsVirtual) return false;
        if (IsKnownVendor(device.VendorId)) return true;
        foreach (var face in device.Interfaces)
            if (face.FeatureLength >= 64 || face.VendorDefined) return true;
        return false;
    }

    public MouseDeviceSettings? TryRead(MouseDevice device)
    {
        string? path = ChoosePath(device, out byte[]? config);
        if (path is null || config is null) return null;
        NoteRates(config);
        bool v2 = IsV2(device.ProductId) || config[OffLod] > 2;
        var settings = new MouseDeviceSettings
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
            ModelName = device.Product,
        };
        bool multiclick = true;
        for (int button = 0; button < ButtonCount; button++)
        {
            byte raw = config[OffButtons + button * 7];
            if (raw > MaxMulticlick && DecodeSpdt(raw) < 0) multiclick = false;
        }
        settings.SupportsMulticlick = multiclick;
        settings.MulticlickLeft = ReadMulticlick(config, 0);
        settings.MulticlickRight = ReadMulticlick(config, 1);
        settings.MulticlickMiddle = ReadMulticlick(config, 2);
        settings.MulticlickForward = ReadMulticlick(config, 3);
        settings.MulticlickBack = ReadMulticlick(config, 4);
        int leftMode = DecodeSpdt(config[OffButtons]);
        int rightMode = DecodeSpdt(config[OffButtons + 7]);
        settings.SupportsSpdt = leftMode >= 0 && rightMode >= 0;
        settings.SpdtLeft = Math.Max(0, leftMode);
        settings.SpdtRight = Math.Max(0, rightMode);
        return settings;
    }

    public IReadOnlyList<MouseSettingSpec> Describe(MouseDevice device, MouseDeviceSettings settings)
    {
        var list = new List<MouseSettingSpec>();
        if (settings is null) return list;
        list.Add(new MouseSettingSpec("motion-sync", "Motion sync", MouseSettingGroup.Sensor, MouseSettingKind.Toggle)
        {
            On = settings.MotionSync,
            Hint = "Aligns reports with the sensor frame.",
        });
        list.Add(new MouseSettingSpec("angle-snapping", "Angle snapping", MouseSettingGroup.Sensor, MouseSettingKind.Toggle)
        {
            On = settings.AngleSnapping,
            Hint = "Straightens near-axis movement.",
        });
        list.Add(new MouseSettingSpec("ripple-control", "Ripple control", MouseSettingGroup.Filters, MouseSettingKind.Toggle)
        {
            On = settings.RippleControl,
            Hint = "Filters click ripple while moving.",
        });
        list.Add(new MouseSettingSpec("slamclick-filter", "Slamclick filter", MouseSettingGroup.Filters, MouseSettingKind.Toggle)
        {
            On = settings.SlamclickFilter,
            Hint = "Ignores switch noise on a hard slam.",
        });
        list.Add(new MouseSettingSpec("jitter-filter", "Motion jitter filter", MouseSettingGroup.Filters, MouseSettingKind.Toggle)
        {
            On = settings.JitterFilter,
            Hint = "Smooths counts while standing still.",
        });
        if (!settings.SupportsMulticlick) return list;
        list.Add(MulticlickRow("multiclick-left", "Left button multiclick filter", settings.MulticlickLeft));
        list.Add(MulticlickRow("multiclick-right", "Right button multiclick filter", settings.MulticlickRight));
        list.Add(MulticlickRow("multiclick-middle", "Middle button multiclick filter", settings.MulticlickMiddle));
        list.Add(MulticlickRow("multiclick-forward", "Forward button multiclick filter", settings.MulticlickForward));
        list.Add(MulticlickRow("multiclick-back", "Back button multiclick filter", settings.MulticlickBack));
        if (!settings.SupportsSpdt) return list;
        list.Add(new MouseSettingSpec("spdt-left", "Left button SPDT", MouseSettingGroup.Buttons, MouseSettingKind.Choice)
        {
            Options = SpdtNames,
            Index = settings.SpdtLeft,
            Hint = "GX modes bypass the filter.",
        });
        list.Add(new MouseSettingSpec("spdt-right", "Right button SPDT", MouseSettingGroup.Buttons, MouseSettingKind.Choice)
        {
            Options = SpdtNames,
            Index = settings.SpdtRight,
            Hint = "GX modes bypass the filter.",
        });
        return list;
    }

    private static MouseSettingSpec MulticlickRow(string id, string label, int value) => new(id, label, MouseSettingGroup.Buttons, MouseSettingKind.Number)
    {
        Number = value,
        Minimum = 0,
        Maximum = MaxMulticlick,
        Step = 1,
        Hint = "Filter strength, not a debounce time.",
    };

    public bool TryWrite(MouseDevice device, string setting, string value)
    {
        string? path = ChoosePath(device, out byte[]? config);
        if (path is null || config is null)
        {
            LastError = LastError.Length > 0 ? LastError : "The configuration interface could not be read.";
            return false;
        }
        NoteRates(config);
        bool v2 = IsV2(device.ProductId) || config[OffLod] > 2;
        switch ((setting ?? string.Empty).ToLowerInvariant())
        {
            case "report-rate":
                if (!int.TryParse(value, out int rate) || Array.IndexOf(Rates, rate) < 0)
                {
                    LastError = "Unsupported report rate.";
                    return false;
                }
                if (rate != DecodeRate(config[OffDivider]) && !RateKnown(_liveRates, rate))
                {
                    LastError = "This mouse did not report " + rate + " Hz, so it was not written.";
                    return false;
                }
                config[OffDivider] = (byte)(8000 / rate);
                if (rate == 8000 && !v2) config[OffMotion] = 0;
                break;
            case "dpi":
                if (!int.TryParse(value, out int dpi) || !IsValidDpi(v2, dpi))
                {
                    LastError = "That DPI value is not supported by this mouse.";
                    return false;
                }
                WriteStages(config, dpi, dpi);
                break;
            case "motion-sync":
                bool motion = string.Equals(value, "On", StringComparison.OrdinalIgnoreCase);
                if (motion && !v2 && DecodeRate(config[OffDivider]) == 8000)
                {
                    LastError = "Motion Sync cannot be on at 8000 Hz on this mouse.";
                    return false;
                }
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
                if (lod < 0 || lod >= LodCount(v2))
                {
                    LastError = "That lift-off value is not supported by this mouse.";
                    return false;
                }
                config[OffLod] = (byte)lod;
                break;
            case "multiclick-left":
            case "multiclick-right":
            case "multiclick-middle":
            case "multiclick-forward":
            case "multiclick-back":
                if (!int.TryParse(value, out int count) || count < 0 || count > MaxMulticlick)
                {
                    LastError = "That multiclick value is not supported by this mouse.";
                    return false;
                }
                int index = ButtonIndex(setting);
                byte raw = config[OffButtons + index * 7];
                if (raw > MaxMulticlick && DecodeSpdt(raw) < 0)
                {
                    LastError = "This mouse does not store a multiclick value for that button.";
                    return false;
                }
                config[OffButtons + index * 7] = (byte)count;
                break;
            case "spdt-left":
            case "spdt-right":
                if (!int.TryParse(value, out int mode) || mode < 0 || mode > 2)
                {
                    LastError = "That SPDT mode is not supported on this button.";
                    return false;
                }
                int record = OffButtons + ButtonIndex(setting) * 7;
                if (DecodeSpdt(config[record]) < 0)
                {
                    LastError = "This mouse does not report SPDT on that button.";
                    return false;
                }
                config[record] = mode == 0 ? SpdtOff : mode == 1 ? SpdtSpeed : SpdtSafe;
                break;
            default:
                return false;
        }
        return StoreConfig(path, config, _liveFeature);
    }

    private void NoteRates(byte[] config)
    {
        int rate = DecodeRate(config[OffDivider]);
        _liveRates = Array.IndexOf(Rates, rate) >= 0 ? Rates : Array.Empty<int>();
    }

    private static int ReadMulticlick(byte[] config, int button)
    {
        byte value = config[OffButtons + button * 7];
        return value > MaxMulticlick ? SpdtOff : value;
    }

    private static int DecodeSpdt(byte value) => value == SpdtSpeed ? 1 : value == SpdtSafe ? 2 : value == SpdtOff ? 0 : -1;

    private static int ButtonIndex(string? setting) => setting switch
    {
        "multiclick-left" or "spdt-left" => 0,
        "multiclick-right" or "spdt-right" => 1,
        "multiclick-middle" => 2,
        "multiclick-forward" => 3,
        "multiclick-back" => 4,
        _ => 0,
    };

    private string? ChoosePath(MouseDevice device, out byte[]? config)
    {
        config = null;
        LastError = string.Empty;
        var paths = CandidatePaths(device);
        if (paths.Count == 0)
        {
            LastError = "No interface to read the mouse from.";
            return null;
        }
        var ranked = new List<(string Path, int Feature, string Problem)>();
        foreach (var face in paths)
        {
            if (face.FeatureLength >= 64)
                ranked.Add((face.DevicePath, face.FeatureLength, string.Empty));
            else
                ranked.Add((face.DevicePath, FeatureLength(face.DevicePath, out string problem), problem));
        }
        ranked.Sort((left, right) => right.Feature.CompareTo(left.Feature));
        var failures = new List<string>();
        foreach (var candidate in ranked)
        {
            if (candidate.Feature < 64)
            {
                failures.Add(ShortPath(candidate.Path) + ": " + (candidate.Feature == 0 ? candidate.Problem : candidate.Feature + " bytes"));
                continue;
            }
            var attempt = ReadConfig(candidate.Path, candidate.Feature);
            if (attempt.Config != null)
            {
                config = attempt.Config;
                _livePath = candidate.Path;
                _liveFeature = candidate.Feature;
                return candidate.Path;
            }
            failures.Add(ShortPath(candidate.Path) + " (" + candidate.Feature + "): " + attempt.Error);
        }
        LastError = failures.Count == 0
            ? "The mouse did not answer the probe."
            : "Tried " + failures.Count + (failures.Count == 1 ? " interface: " : " interfaces: ") + string.Join("; ", failures);
        return null;
    }

    private static List<MouseInterfaceInfo> CandidatePaths(MouseDevice device)
    {
        var ordered = new List<MouseInterfaceInfo>();
        var others = new List<MouseInterfaceInfo>();
        foreach (var face in device.Interfaces)
        {
            if (face.DevicePath.Length == 0) continue;
            bool duplicate = false;
            foreach (var known in ordered)
                if (string.Equals(known.DevicePath, face.DevicePath, StringComparison.OrdinalIgnoreCase)) duplicate = true;
            foreach (var known in others)
                if (string.Equals(known.DevicePath, face.DevicePath, StringComparison.OrdinalIgnoreCase)) duplicate = true;
            if (duplicate) continue;
            if (face.VendorDefined) ordered.Add(face);
            else others.Add(face);
        }
        ordered.Sort((left, right) => right.FeatureLength.CompareTo(left.FeatureLength));
        others.Sort((left, right) => right.FeatureLength.CompareTo(left.FeatureLength));
        foreach (var face in others) ordered.Add(face);
        return ordered;
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
        if (dpi < 10 || dpi > 30000) return false;
        int step = v2 ? (dpi <= 10000 ? 10 : 50) : 50;
        return dpi % step == 0;
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

    private static bool IsValidConfig(byte[] config, int length)
    {
        if (config is null || length < 131) return false;
        if (config[1] != StatusOk) return false;
        int levels = config[OffLevels];
        if (levels < 1 || levels > 4) return false;
        if (config[OffDivider] < 1) return false;
        int dpi = ReadU16(config, OffStages + 1);
        return dpi >= 10 && dpi <= 30000;
    }

    private static string NormalizePath(string path)
    {
        string clean = (path ?? string.Empty).TrimEnd('\0').Trim();
        if (clean.EndsWith("\\kbd", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 4);
        if (clean.StartsWith("\\??\\", StringComparison.Ordinal)) clean = clean.Substring(4);
        else if (clean.StartsWith("\\\\?\\", StringComparison.Ordinal)) clean = clean.Substring(4);
        else if (clean.StartsWith("\\\\.\\", StringComparison.Ordinal)) clean = clean.Substring(4);
        if (clean.StartsWith("\\", StringComparison.Ordinal)) clean = clean.TrimStart('\\');
        return "\\\\?\\" + clean;
    }

    private static IntPtr OpenPath(string path, uint access)
    {
        const uint share = 3;
        const uint open = 3;
        string full = NormalizePath(path);
        IntPtr handle = NativeInput.CreateFileW(full, access, share, IntPtr.Zero, open, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return IntPtr.Zero;
        return handle;
    }

    private static IntPtr OpenForRead(string path)
    {
        foreach (uint access in new uint[] { 0xC0000000, 0x80000000, 0 })
        {
            IntPtr handle = OpenPath(path, access);
            if (handle != IntPtr.Zero) return handle;
        }
        return IntPtr.Zero;
    }

    private static int FeatureLength(string path, out string problem)
    {
        problem = "could not open";
        IntPtr handle = OpenForRead(path);
        if (handle == IntPtr.Zero)
        {
            problem = "open refused (code " + Marshal.GetLastWin32Error() + ")";
            return 0;
        }
        try
        {
            if (!NativeInput.HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            {
                problem = "descriptor refused (code " + Marshal.GetLastWin32Error() + ")";
                return 0;
            }
            try
            {
                IntPtr caps = Marshal.AllocHGlobal(64);
                try
                {
                    for (int i = 0; i < 64; i++) Marshal.WriteByte(caps, i, 0);
                    int status = NativeInput.HidP_GetCaps(preparsed, caps);
                    if (status < 0)
                    {
                        problem = "caps status 0x" + status.ToString("X8");
                        return 0;
                    }
                    int feature = (ushort)Marshal.ReadInt16(caps, 8);
                    problem = "no feature reports";
                    return feature;
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                }
            }
            finally
            {
                try { NativeInput.HidD_FreePreparsedData(preparsed); } catch { }
            }
        }
        finally
        {
            try { NativeInput.CloseHandle(handle); } catch { }
        }
    }

    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(none)";
        int hash = path.LastIndexOf('#');
        string tail = hash >= 0 && hash + 1 < path.Length ? path.Substring(hash + 1) : path;
        int brace = tail.IndexOf('{');
        if (brace > 0) tail = tail.Substring(0, brace);
        if (tail.Length > 20) tail = tail.Substring(0, 20);
        int col = path.IndexOf("col", StringComparison.OrdinalIgnoreCase);
        string collection = col >= 0 && col + 5 <= path.Length ? path.Substring(col, 5) : string.Empty;
        return collection.Length > 0 ? collection + "/" + tail : tail;
    }

    private sealed class ReadAttempt
    {
        public byte[]? Config;
        public string Error = string.Empty;
    }

    private static ReadAttempt ReadConfig(string path, int feature)
    {
        var attempt = new ReadAttempt();
        IntPtr handle = OpenForRead(path);
        if (handle == IntPtr.Zero)
        {
            attempt.Error = "open refused";
            return attempt;
        }
        try
        {
            var command = new byte[feature];
            command[0] = ReportCommand;
            command[1] = OpLoad;
            bool sent = SetReport(handle, command);
            int sendError = sent ? 0 : Marshal.GetLastWin32Error();
            System.Threading.Thread.Sleep(120);
            for (int round = 0; round < 3; round++)
            {
                foreach (byte id in new byte[] { ReportCommand, ReportConfig })
                {
                    byte[]? got = GetReport(handle, id, feature);
                    if (got != null && IsValidConfig(got, got.Length))
                    {
                        got[0] = ReportConfig;
                        attempt.Config = got;
                        return attempt;
                    }
                }
                System.Threading.Thread.Sleep(80 << round);
            }
            attempt.Error = sent ? "no valid configuration" : "load refused (code " + sendError + ")";
            return attempt;
        }
        finally
        {
            try { NativeInput.CloseHandle(handle); } catch { }
        }
    }

    private static bool StoreConfig(string path, byte[] config, int feature)
    {
        if (feature <= 0) return false;
        IntPtr handle = OpenForRead(path);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new byte[feature];
            buffer[0] = ReportConfig;
            buffer[1] = OpStore;
            int copy = Math.Min(config.Length - 2, feature - 2);
            if (copy > 0) Buffer.BlockCopy(config, 2, buffer, 2, copy);
            if (!SetReport(handle, buffer)) return false;
            System.Threading.Thread.Sleep(300);
            int backoff = 60;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[]? reply = GetReport(handle, ReportCommand, feature);
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
            if (!NativeInput.HidD_GetFeature(handle, native, (uint)size)) return null;
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
