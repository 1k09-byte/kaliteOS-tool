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
    internal const uint SpdrpDeviceDesc = 0x00000000;
    internal const uint SpdrpClassName = 0x00000007;
    internal const uint SpdrpFriendlyName = 0x0000000C;
    internal const uint SpdrpLocationInfo = 0x0000000D;
    internal const uint SpdrpAddress = 0x0000001C;
    internal const uint DigcfAllClasses = 0x00000004;

    internal static readonly Guid DevpkeyDeviceContainerIdFmtid =
        new(0x8c7ed8ab, 0x3f4c, 0x11d2, 0xb2, 0x7d, 0x00, 0x10, 0x4b, 0xd1, 0x79, 0xd4);
    internal const uint DevpkeyDeviceContainerIdPid = 2;

    internal static readonly Guid DevpkeyDeviceParentFmtid =
        new(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7);
    internal const uint DevpkeyDeviceParentPid = 8;

    internal static readonly Guid DevpkeyDeviceBusDescFmtid =
        new(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2);
    internal const uint DevpkeyDeviceBusDescPid = 4;

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
    internal static partial uint GetRawInputData(
        IntPtr rawInput,
        uint command,
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTimeValue
    {
        public uint Low;
        public uint High;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(
        out FileTimeValue idle,
        out FileTimeValue kernel,
        out FileTimeValue user);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetThreadTimes(
        IntPtr thread,
        out FileTimeValue creation,
        out FileTimeValue exit,
        out FileTimeValue kernel,
        out FileTimeValue user);

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

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll", SetLastError = true)]
    internal static partial int HidP_GetCaps(IntPtr preparsedData, IntPtr capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        IntPtr deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

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
        public IntPtr Proc;
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
    internal static partial bool PeekMessageW(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    internal const uint QsAllInput = 0x04FF;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint MsgWaitForMultipleObjectsEx(
        uint count,
        IntPtr handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

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
