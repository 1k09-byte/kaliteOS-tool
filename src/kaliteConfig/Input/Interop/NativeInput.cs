using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Input.Interop;

internal static partial class NativeInput
{
    internal const uint RimTypeMouse = 0;
    internal const uint RimTypeKeyboard = 1;
    internal const uint RimTypeHid = 2;

    internal const uint RidiDevicename = 0x20000007;
    internal const uint RidiDeviceinfo = 0x2000000B;

    internal const uint RidInput = 0x10000003;
    internal const uint RidHeader = 0x10000005;
    internal const uint RidevInputSink = 0x00000100;
    internal const uint RidevRemove = 0x00000001;
    internal const ushort HidUsagePageGeneric = 0x01;
    internal const ushort HidUsageGenericMouse = 0x02;

    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceinterface = 0x00000010;

    internal const uint SpdrpManufacturer = 0x0000000B;

    internal static readonly Guid DevpkeyDeviceContainerIdFmtid =
        new(0x8c7ed8ab, 0x3f4c, 0x11d2, 0xb2, 0x7d, 0x00, 0x10, 0x4b, 0xd1, 0x79, 0xd4);
    internal const uint DevpkeyDeviceContainerIdPid = 2;

    internal static readonly Guid HidInterfaceGuid = new(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceList
    {
        public IntPtr Handle;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceInfoMouse
    {
        public uint Id;
        public uint NumberOfButtons;
        public uint SampleRate;
        [MarshalAs(UnmanagedType.Bool)]
        public bool HasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceInfoHid
    {
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawMouse
    {
        public ushort Flags;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    internal delegate void DeviceNotifyCallback(
        IntPtr notifyContext,
        uint notifyType,
        IntPtr notifyInfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputDeviceList(
        IntPtr rawInputDeviceList,
        ref uint numDevices,
        uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputDeviceInfoW(
        IntPtr device,
        uint command,
        IntPtr data,
        ref uint dataSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetRawInputBuffer(
        IntPtr data,
        ref uint size,
        uint headerSize);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numDevices,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial long QueryPerformanceCounter(out long value);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial long QueryPerformanceFrequency(out long value);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("hid.dll", SetLastError = true)]
    internal static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HidAttributes attributes);

    [LibraryImport("hid.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetManufacturerString(IntPtr hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetProductString(IntPtr hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, IntPtr buffer, uint bufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_SetFeature(IntPtr hidDeviceObject, IntPtr reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetFeature(IntPtr hidDeviceObject, IntPtr reportBuffer, uint reportBufferLength);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref DevPropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("cfgmgr32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Register_Notification(
        IntPtr filter,
        IntPtr context,
        DeviceNotifyCallback callback,
        out IntPtr notifyContext);

    [LibraryImport("cfgmgr32.dll", SetLastError = true)]
    internal static partial uint CM_Unregister_Notification(IntPtr notifyContext);

    internal delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc Proc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetMessageW(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr DispatchMessageW(ref Msg message);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandleW(string? moduleName);
}
