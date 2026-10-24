using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using kaliteConfig.Input.Interop;

namespace kaliteConfig.Input;

public interface IMouseDiscoveryService
{
    IReadOnlyList<MouseDevice> Devices { get; }
    int DroppedInterfaces { get; }
    string LastScanError { get; }
    event Action? Changed;
    void Refresh();
    void StartNotifications();
    void StopNotifications();
}

public sealed class MouseDiscoveryService : IMouseDiscoveryService, IDisposable
{
    private readonly MouseCatalog _catalog;
    private readonly IMouseClassifier _classifier;
    private readonly IVendorSoftwareDetector _vendor;
    private readonly List<MouseDevice> _devices = new();
    private readonly object _gate = new();
    private IntPtr _notifyContext;
    private byte[]? _filterBuffer;
    private System.Runtime.InteropServices.GCHandle _filterPin;
    private NativeInput.DeviceNotifyCallback? _callback;
    private bool _disposed;

    public MouseDiscoveryService(MouseCatalog catalog, IMouseClassifier classifier, IVendorSoftwareDetector vendor)
    {
        _catalog = catalog;
        _classifier = classifier;
        _vendor = vendor;
    }

    public IReadOnlyList<MouseDevice> Devices
    {
        get { lock (_gate) return _devices.ToArray(); }
    }

    public int DroppedInterfaces
    {
        get { lock (_gate) return _dropped; }
    }

    public string LastScanError
    {
        get { lock (_gate) return _scanError; }
    }

    private int _dropped;
    private string _scanError = string.Empty;

    public event Action? Changed;

    public void Refresh()
    {
        var found = Enumerate();
        var (present, name) = _vendor.Detect(_catalog);
        foreach (var device in found)
        {
            _classifier.Classify(device, _catalog, present && MakerMatches(device, name), present ? name : string.Empty, HasHandledVendorInterface(device));
            device.DiscoveryText = DescribeDiscovery(device);
        }
        lock (_gate)
        {
            _devices.Clear();
            _devices.AddRange(found);
        }
        try { Changed?.Invoke(); } catch { }
    }

    private static string ConnectionText(MouseConnection connection) => connection switch
    {
        MouseConnection.Usb => "USB",
        MouseConnection.Receiver24Ghz => "2.4 GHz receiver",
        MouseConnection.Bluetooth => "Bluetooth",
        MouseConnection.Other => "Other",
        _ => "Unknown",
    };

    private static string DescribeDiscovery(MouseDevice device)
    {
        return "VID " + device.VendorId.ToString("X4") + ", PID " + device.ProductId.ToString("X4") + "\n"
            + "Maker: " + (device.Manufacturer.Length > 0 ? device.Manufacturer : "not reported") + " (" + device.MakerSource + ")\n"
            + "Product: " + (device.Product.Length > 0 ? device.Product : "not reported") + "\n"
            + "Serial: " + (device.Serial.Length > 0 ? device.Serial : "not exposed") + "\n"
            + "Container: " + (device.ContainerId.Length > 0 ? device.ContainerId : "none") + "\n"
            + "Path: " + device.InterfacePath + "\n"
            + "Connection: " + ConnectionText(device.Connection) + "\n"
            + "Open: " + device.OpenResult + "\n"
            + "Rule: " + device.ClassReason;
    }

    private static bool MakerMatches(MouseDevice device, string softwareName)
    {
        if (string.IsNullOrEmpty(softwareName)) return false;
        string maker = device.Manufacturer ?? string.Empty;
        if (maker.Length == 0) return false;
        string lower = softwareName.ToLowerInvariant();
        foreach (string word in maker.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            if (word.Length > 2 && lower.Contains(word.ToLowerInvariant())) return true;
        return false;
    }

    private static bool HasHandledVendorInterface(MouseDevice device)
    {
        return false;
    }

    public void StartNotifications()
    {
        if (_notifyContext != IntPtr.Zero) return;
        try
        {
            _filterBuffer = new byte[48];
            BitConverter.GetBytes(48u).CopyTo(_filterBuffer, 0);
            Guid hid = NativeInput.HidInterfaceGuid;
            hid.ToByteArray().CopyTo(_filterBuffer, 32);
            _callback = OnNotify;
            _filterPin = GCHandle.Alloc(_filterBuffer, GCHandleType.Pinned);
            IntPtr filter = _filterPin.AddrOfPinnedObject();
            uint result = NativeInput.CM_Register_Notification(filter, IntPtr.Zero, _callback, out _notifyContext);
            if (result != 0)
            {
                _notifyContext = IntPtr.Zero;
                ReleaseFilterPin();
            }
        }
        catch
        {
            _notifyContext = IntPtr.Zero;
            ReleaseFilterPin();
        }
    }

    public void StopNotifications()
    {
        if (_notifyContext == IntPtr.Zero) return;
        try { NativeInput.CM_Unregister_Notification(_notifyContext); }
        catch { }
        _notifyContext = IntPtr.Zero;
        _callback = null;
        ReleaseFilterPin();
        _filterBuffer = null;
    }

    private void ReleaseFilterPin()
    {
        try
        {
            if (_filterPin.IsAllocated) _filterPin.Free();
        }
        catch { }
    }

    private void OnNotify(IntPtr context, uint notifyType, IntPtr notifyInfo)
    {
        try { Refresh(); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopNotifications();
    }

    private static uint RawListCount()
    {
        uint count = 0;
        NativeInput.GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf<NativeInput.RawInputDeviceList>());
        return count;
    }

    private List<MouseDevice> Enumerate()
    {
        _dropped = 0;
        string? firstError = null;
        _scanError = string.Empty;
        var devices = new List<MouseDevice>();
        var mouseInfos = new List<RawInterface>();
        var auxInfos = new List<RawInterface>();
        try
        {
            uint count = RawListCount();
            if (count == 0) return devices;
            int entrySize = Marshal.SizeOf<NativeInput.RawInputDeviceList>();
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * entrySize));
            try
            {
                uint want = count;
                uint got = NativeInput.GetRawInputDeviceList(buffer, ref want, (uint)entrySize);
                if (got == unchecked((uint)-1)) return devices;
                for (int i = 0; i < got; i++)
                {
                    IntPtr item = IntPtr.Add(buffer, i * entrySize);
                    var list = Marshal.PtrToStructure<NativeInput.RawInputDeviceList>(item);
                    var info = ReadInterface(list.Handle, list.Type, ref firstError);
                    if (info is null) { _dropped++; continue; }
                    info.Handle = (ulong)list.Handle.ToInt64();
                    if (list.Type == NativeInput.RimTypeMouse) mouseInfos.Add(info);
                    else if (list.Type == NativeInput.RimTypeKeyboard) auxInfos.Add(info);
                    else if (list.Type == NativeInput.RimTypeHid && info.VendorDefined) auxInfos.Add(info);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return devices;
        }
        var setupPaths = MapSetupPaths();
        var auxByContainer = new Dictionary<string, List<MouseInterfaceInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var aux in auxInfos)
        {
            if (!setupPaths.TryGetValue(NormPath(aux.Path), out var setup)) continue;
            if (setup.ContainerId.Length == 0) continue;
            var entry = new MouseInterfaceInfo
            {
                DevicePath = aux.Path,
                Kind = aux.Kind,
                VendorId = aux.VendorId,
                ProductId = aux.ProductId,
                UsagePage = aux.UsagePage,
                Usage = aux.Usage,
                VendorDefined = aux.VendorDefined,
            };
            if (!auxByContainer.TryGetValue(setup.ContainerId, out var list))
            {
                list = new List<MouseInterfaceInfo>();
                auxByContainer[setup.ContainerId] = list;
            }
            list.Add(entry);
        }
        foreach (var info in mouseInfos)
        {
            if (MouseClassifier.LooksLikeNonMouse(info.ManufacturerText, info.ProductText)) continue;
            var device = BuildDevice(info, setupPaths);
            if (device is null) continue;
            if (device.ContainerId.Length > 0 && auxByContainer.TryGetValue(device.ContainerId, out var extra))
                foreach (var extraInfo in extra)
                    device.Interfaces.Add(extraInfo);
            devices.Add(device);
        }
        MarkReceivers(devices);
        _scanError = firstError ?? string.Empty;
        return devices;
    }

    private sealed class RawInterface
    {
        public ulong Handle;
        public bool Opened;
        public int OpenError;
        public string Path = string.Empty;
        public string ManufacturerText = string.Empty;
        public string ProductText = string.Empty;
        public string SerialText = string.Empty;
        public uint Kind;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public bool VendorDefined;
        public uint Buttons;
    }

    private static RawInterface? ReadInterface(IntPtr handle, uint kind, ref string? firstError)
    {
        var result = new RawInterface { Kind = kind };
        try
        {
            uint nameSize = 0;
            uint sizeResult = NativeInput.GetRawInputDeviceInfoW(handle, NativeInput.RidiDevicename, IntPtr.Zero, ref nameSize);
            if (nameSize < 2)
            {
                int code = Marshal.GetLastWin32Error();
                firstError ??= "Reads failed at the device-name size call (result " + sizeResult + ", code " + code + ").";
                return null;
            }
            IntPtr nameBuffer = Marshal.AllocHGlobal(checked((int)nameSize * 2));
            try
            {
                uint nameLen = nameSize;
                if (NativeInput.GetRawInputDeviceInfoW(handle, NativeInput.RidiDevicename, nameBuffer, ref nameLen) == unchecked((uint)-1))
                {
                    int code = Marshal.GetLastWin32Error();
                    firstError ??= "Reads failed at the device-name call (code " + code + ").";
                    return null;
                }
                result.Path = Marshal.PtrToStringUni(nameBuffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(nameBuffer);
            }
            if (result.Path.Length == 0)
            {
                firstError ??= "Reads failed: the device name came back empty.";
                return null;
            }
            IntPtr infoBuffer = Marshal.AllocHGlobal(128);
            try
            {
                byte[] blank = new byte[128];
                Marshal.Copy(blank, 0, infoBuffer, 128);
                Marshal.WriteInt32(infoBuffer, 0, 32);
                uint infoSize = 128;
                if (NativeInput.GetRawInputDeviceInfoW(handle, NativeInput.RidiDeviceinfo, infoBuffer, ref infoSize) == unchecked((uint)-1))
                {
                    int code = Marshal.GetLastWin32Error();
                    firstError ??= "Reads failed at the device-info call (code " + code + ").";
                    return null;
                }
                uint type = (uint)Marshal.ReadInt32(infoBuffer, 4);
                if (type == NativeInput.RimTypeMouse)
                {
                    result.Buttons = (uint)Marshal.ReadInt32(infoBuffer, 12);
                }
                else if (type == NativeInput.RimTypeKeyboard)
                {
                    result.VendorDefined = false;
                }
                else if (type == NativeInput.RimTypeHid)
                {
                    result.UsagePage = (ushort)Marshal.ReadInt16(infoBuffer, 20);
                    result.Usage = (ushort)Marshal.ReadInt16(infoBuffer, 22);
                    result.VendorDefined = result.UsagePage >= 0xFF00;
                    result.VendorId = (ushort)Marshal.ReadInt16(infoBuffer, 8);
                    result.ProductId = (ushort)Marshal.ReadInt16(infoBuffer, 12);
                }
                else
                {
                    firstError ??= "Reads failed: unknown device type " + type + ".";
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoBuffer);
            }
            ReadHidStrings(result);
            return result;
        }
        catch (Exception ex)
        {
            firstError ??= "Reads failed: " + ex.GetType().Name + " " + ex.Message;
            return null;
        }
    }

    private static void ReadHidStrings(RawInterface result)
    {
        const uint share = 3;
        const uint open = 3;
        IntPtr handle = NativeInput.CreateFileW(result.Path, 0, share, IntPtr.Zero, open, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            result.Opened = false;
            try { result.OpenError = Marshal.GetLastWin32Error(); }
            catch { result.OpenError = -1; }
            return;
        }
        result.Opened = true;
        try
        {
            var attributes = new NativeInput.HidAttributes { Size = (uint)Marshal.SizeOf<NativeInput.HidAttributes>() };
            if (NativeInput.HidD_GetAttributes(handle, ref attributes))
            {
                result.VendorId = attributes.VendorId;
                result.ProductId = attributes.ProductId;
            }
            result.ManufacturerText = ReadHidText(handle, true);
            result.ProductText = ReadHidText(handle, false);
            result.SerialText = ReadHidSerial(handle);
        }
        catch { }
        finally
        {
            try { NativeInput.CloseHandle(handle); } catch { }
        }
    }

    private static string ReadHidText(IntPtr handle, bool maker)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            bool ok = maker
                ? NativeInput.HidD_GetManufacturerString(handle, buffer, 512)
                : NativeInput.HidD_GetProductString(handle, buffer, 512);
            if (!ok) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadHidSerial(IntPtr handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (!NativeInput.HidD_GetSerialNumberString(handle, buffer, 512)) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed class SetupEntry
    {
        public string DetailPath = string.Empty;
        public string ContainerId = string.Empty;
        public string Maker = string.Empty;
    }

    private static Dictionary<string, SetupEntry> MapSetupPaths()
    {
        var map = new Dictionary<string, SetupEntry>(StringComparer.OrdinalIgnoreCase);
        Guid hid = NativeInput.HidInterfaceGuid;
        IntPtr set = NativeInput.SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero,
            NativeInput.DigcfPresent | NativeInput.DigcfDeviceinterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return map;
        try
        {
            uint index = 0;
            while (true)
            {
                var iface = new NativeInput.DeviceInterfaceData { Size = (uint)Marshal.SizeOf<NativeInput.DeviceInterfaceData>() };
                if (!NativeInput.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref iface)) break;
                index++;
                uint required = 0;
                NativeInput.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required < 8) continue;
                IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    IntPtr devinfo = Marshal.AllocHGlobal(28);
                    try
                    {
                        Marshal.WriteInt32(devinfo, 28);
                        uint got = 0;
                        if (!NativeInput.SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, required, out got, devinfo)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, IntPtr.Size == 8 ? 8 : 4)) ?? string.Empty;
                        if (path.Length == 0) continue;
                        var entry = new SetupEntry { DetailPath = path };
                        entry.ContainerId = ReadContainer(set, devinfo);
                        entry.Maker = ReadMaker(set, devinfo);
                        map[NormPath(path)] = entry;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(devinfo);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        catch { }
        finally
        {
            try { NativeInput.SetupDiDestroyDeviceInfoList(set); } catch { }
        }
        return map;
    }

    private static string NormPath(string path)
    {
        string text = (path ?? string.Empty).Trim();
        if (text.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4);
        return text;
    }

    private static string ReadContainer(IntPtr set, IntPtr devinfo)
    {
        try
        {
            var key = new NativeInput.DevPropKey { Fmtid = NativeInput.DevpkeyDeviceContainerIdFmtid, Pid = NativeInput.DevpkeyDeviceContainerIdPid };
            IntPtr buffer = Marshal.AllocHGlobal(64);
            try
            {
                if (!NativeInput.SetupDiGetDevicePropertyW(set, devinfo, ref key, out _, buffer, 64, out _, 0)) return string.Empty;
                var guid = Marshal.PtrToStructure<Guid>(buffer);
                return guid == Guid.Empty ? string.Empty : guid.ToString("B").ToUpperInvariant();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { return string.Empty; }
    }

    private static string ReadMaker(IntPtr set, IntPtr devinfo)
    {
        try
        {
            uint needed = 0;
            uint regtype = 0;
            NativeInput.SetupDiGetDeviceRegistryPropertyW(set, devinfo, NativeInput.SpdrpManufacturer, out regtype, IntPtr.Zero, 0, out needed);
            if (needed < 2) return string.Empty;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!NativeInput.SetupDiGetDeviceRegistryPropertyW(set, devinfo, NativeInput.SpdrpManufacturer, out regtype, buffer, needed, out _)) return string.Empty;
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { return string.Empty; }
    }

    private MouseDevice? BuildDevice(RawInterface info, Dictionary<string, SetupEntry> setupPaths)
    {
        try
        {
            ParseHidPath(info.Path, out ushort vid, out ushort pid, out string instance);
            if (info.VendorId != 0) vid = info.VendorId;
            if (info.ProductId != 0) pid = info.ProductId;
            string maker = info.ManufacturerText;
            string source = maker.Length > 0 ? "HID string descriptor" : "unknown";
            string container = string.Empty;
            if (setupPaths.TryGetValue(NormPath(info.Path), out var setup))
            {
                container = setup.ContainerId;
                if (maker.Length == 0 && setup.Maker.Length > 0)
                {
                    maker = setup.Maker;
                    source = "device node";
                }
            }
            var device = new MouseDevice
            {
                VendorId = vid,
                ProductId = pid,
                Manufacturer = maker.Trim(),
                Product = info.ProductText.Trim(),
                Serial = info.SerialText.Trim(),
                ContainerId = container,
            };
            device.IsVirtual = _catalog.LooksVirtual(instance, string.Empty, info.Path);
            string serialPart = device.Serial.Length > 0 ? device.Serial : container.Length > 0 ? container : instance;
            device.StableId = vid.ToString("X4") + ":" + pid.ToString("X4") + ":" + serialPart;
            device.DisplayName = device.Product.Length > 0 && !_catalog.IsGenericProduct(device.Product)
                ? device.Product : "Mouse " + vid.ToString("X4") + ":" + pid.ToString("X4");
            device.BrandText = device.Manufacturer;
            device.InterfacePath = info.Path;
            device.MakerSource = source;
            device.OpenResult = info.Opened
                ? "Interface opened for string reads."
                : "Interface could not be opened (code " + info.OpenError + "). Device-node values used.";
            device.Connection = ClassifyConnection(info.Path, instance);
            device.Interfaces.Add(new MouseInterfaceInfo
            {
                Handle = info.Handle,
                DevicePath = info.Path,
                InstanceId = instance,
                Kind = info.Kind,
                VendorId = vid,
                ProductId = pid,
                VendorDefined = false,
            });
            return device;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseHidPath(string path, out ushort vid, out ushort pid, out string instance)
    {
        vid = 0;
        pid = 0;
        instance = string.Empty;
        string text = (path ?? string.Empty).ToUpperInvariant();
        int v = text.IndexOf("VID_", StringComparison.Ordinal);
        if (v >= 0 && v + 8 <= text.Length)
            ushort.TryParse(text.Substring(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid);
        int p = text.IndexOf("PID_", StringComparison.Ordinal);
        if (p >= 0 && p + 8 <= text.Length)
            ushort.TryParse(text.Substring(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
        string[] parts = (path ?? string.Empty).Split('#');
        if (parts.Length >= 3) instance = parts[2];
    }

    private static MouseConnection ClassifyConnection(string path, string instance)
    {
        string text = ((path ?? string.Empty) + "|" + (instance ?? string.Empty)).ToUpperInvariant();
        if (text.Contains("BTHENUM") || text.Contains("BTHUSB") || text.Contains("BLUETOOTH")) return MouseConnection.Bluetooth;
        if (text.Contains("USB")) return MouseConnection.Usb;
        if (text.Contains("HID")) return MouseConnection.Other;
        return MouseConnection.Unknown;
    }

    private static void MarkReceivers(List<MouseDevice> devices)
    {
        foreach (var device in devices)
        {
            bool keyboard = false;
            bool multi = device.Interfaces.Count > 1;
            if (!multi) continue;
            foreach (var iface in device.Interfaces)
                if (iface.Kind == NativeInput.RimTypeKeyboard) keyboard = true;
            if (keyboard && device.Connection == MouseConnection.Usb)
            {
                device.BehindReceiver = true;
                device.Connection = MouseConnection.Receiver24Ghz;
            }
        }
    }
}
