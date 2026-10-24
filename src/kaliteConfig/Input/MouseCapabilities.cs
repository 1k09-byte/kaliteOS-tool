using System;
using System.Collections.Generic;

namespace kaliteConfig.Input;

public enum CapabilityKind
{
    Sensor,
    Buttons,
    Scroll,
    Lighting,
    Power,
    Profiles,
    DeviceInfo,
    ReportRate,
    WindowsSettings,
    Test,
    Log,
}

public enum CapabilityState
{
    Available,
    AvailableUnverified,
    Unsupported,
    NotImplemented,
    CantRead,
    ControlledByVendor,
}

public sealed class MouseCapability
{
    public MouseCapability(CapabilityKind kind, CapabilityState state, string detail)
    {
        Kind = kind;
        State = state;
        Detail = detail ?? string.Empty;
    }

    public CapabilityKind Kind { get; }
    public CapabilityState State { get; }
    public string Detail { get; }

    public string Title => Kind switch
    {
        CapabilityKind.Sensor => "Sensor",
        CapabilityKind.Buttons => "Buttons",
        CapabilityKind.Scroll => "Scroll",
        CapabilityKind.Lighting => "Lighting",
        CapabilityKind.Power => "Power",
        CapabilityKind.Profiles => "Profiles",
        CapabilityKind.DeviceInfo => "Info",
        CapabilityKind.ReportRate => "Report rate",
        CapabilityKind.WindowsSettings => "Windows",
        CapabilityKind.Test => "Test",
        _ => "Log",
    };

    public string StateText => State switch
    {
        CapabilityState.Available => "Available and verified",
        CapabilityState.AvailableUnverified => "Available, not verified on this device",
        CapabilityState.Unsupported => "Not supported by this mouse",
        CapabilityState.NotImplemented => "Not implemented in this app",
        CapabilityState.CantRead => "Can't read",
        _ => "Controlled by vendor software",
    };
}

public interface IMouseCapabilityProvider
{
    IReadOnlyList<MouseCapability> ForDevice(MouseDevice device);
}

public sealed class MouseCapabilityProvider : IMouseCapabilityProvider
{
    public IReadOnlyList<MouseCapability> ForDevice(MouseDevice device)
    {
        var list = new List<MouseCapability>();
        if (device.Class == MouseClass.Ignored && !device.ForceShow)
            return list;
        list.Add(new MouseCapability(CapabilityKind.DeviceInfo, CapabilityState.Available, string.Empty));
        list.Add(new MouseCapability(CapabilityKind.WindowsSettings, CapabilityState.Available, "Applies to all mice."));
        bool vendorControlled = device.VendorSoftwarePresent;
        var vendorState = vendorControlled ? CapabilityState.ControlledByVendor : CapabilityState.NotImplemented;
        string vendorDetail = vendorControlled
            ? device.VendorSoftwareName + " is running and may overwrite changes made here."
            : "Detected, vendor settings not implemented yet.";
        list.Add(new MouseCapability(CapabilityKind.Sensor, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.Buttons, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.Scroll, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.Lighting, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.Power, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.Profiles, vendorState, vendorDetail));
        list.Add(new MouseCapability(CapabilityKind.ReportRate, CapabilityState.Available, "Setting: can't read from this mouse. No verified module."));
        list.Add(new MouseCapability(CapabilityKind.Test, CapabilityState.Available, string.Empty));
        list.Add(new MouseCapability(CapabilityKind.Log, CapabilityState.Available, string.Empty));
        return list;
    }
}
