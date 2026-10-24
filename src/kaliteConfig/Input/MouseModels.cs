using System;
using System.Collections.Generic;

namespace kaliteConfig.Input;

public enum MouseConnection
{
    Unknown,
    Usb,
    Receiver24Ghz,
    Bluetooth,
    Other,
}

public enum MouseClass
{
    Unclassified,
    Supported,
    Ignored,
}

public enum MouseResult
{
    Unknown,
    Verified,
    AppliedNoVerify,
    Refused,
    Reverted,
    Locked,
    Unsupported,
    NotImplemented,
    Blocked,
    CantRead,
    Drifted,
    RestoreFailed,
}

public sealed class MouseInterfaceInfo
{
    public ulong Handle { get; set; }
    public string DevicePath { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public uint Kind { get; set; }
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public ushort UsagePage { get; set; }
    public ushort Usage { get; set; }
    public bool VendorDefined { get; set; }
}

public sealed class MouseDevice
{
    public string StableId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BrandText { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public string ContainerId { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public MouseConnection Connection { get; set; }
    public MouseClass Class { get; set; }
    public string ClassReason { get; set; } = string.Empty;
    public bool Branded { get; set; }
    public bool VendorSoftwarePresent { get; set; }
    public string VendorSoftwareName { get; set; } = string.Empty;
    public bool BehindReceiver { get; set; }
    public bool IsVirtual { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public bool ForceShow { get; set; }
    public List<MouseInterfaceInfo> Interfaces { get; } = new();
}
