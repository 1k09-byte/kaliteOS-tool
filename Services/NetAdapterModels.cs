using System;
using System.Collections.Generic;

namespace kaliteConfig.Services;

// NOTE: types bound in XAML ({x:Bind}) must be classes with get+set properties;
// positional records are init-only and break XamlTypeInfo generation.

public enum NetAdapterKind { Ethernet, WiFi, Virtual, Vpn, Other }

public sealed record NetWifiInfo(
    bool Present,
    string StatusMessage,
    string Ssid,
    string Bssid,
    string Band,
    string Channel,
    string Phy,
    string Signal,
    string Rssi,
    string RxRate,
    string TxRate,
    string Security);

public sealed record NetStatSample(DateTime AtUtc, long RxBytes, long TxBytes);

public enum NetFindingSeverity { Info, Warning }

public sealed class NetAdapterInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public NetAdapterKind Kind { get; set; }
    public string Status { get; set; } = "";
    public bool IsUp { get; set; }
    public long LinkSpeedBps { get; set; }
    public string Mac { get; set; } = "";
    public string RegistryKey { get; set; } = "";
    public List<string> IPv4 { get; set; } = new();
    public List<string> IPv6 { get; set; } = new();
    public List<string> Gateways { get; set; } = new();
    public List<string> Dns { get; set; } = new();
    public bool DhcpEnabled { get; set; }
    public string DhcpServer { get; set; } = "";
    public string Mtu { get; set; } = "";
    public int InterfaceIndex { get; set; }
}

public sealed class NetAdvancedOption
{
    public NetAdvancedOption() { }
    public NetAdvancedOption(string value, string description) { Value = value; Description = description; }
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class NetAdvancedProp
{
    public NetAdvancedProp() { Options = new List<NetAdvancedOption>(); }
    public string Keyword { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Current { get; set; } = "";
    public string Default { get; set; } = "";
    public List<NetAdvancedOption> Options { get; set; } = new();
    public long? Min { get; set; }
    public long? Max { get; set; }
    public long? Step { get; set; }
    public string Category { get; set; } = "";
    public bool Changed { get; set; }
    public string Explanation { get; set; } = "";
}

public sealed class NetTcpEntry
{
    public NetTcpEntry() { }
    public NetTcpEntry(string name, string value, string baseline) { Name = name; Value = value; Baseline = baseline; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Baseline { get; set; } = "";
}

public sealed class NetDoctorFinding
{
    public NetDoctorFinding() { }
    public NetFindingSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? FixKeyword { get; set; }
    public string? FixValue { get; set; }
    public string? FixDescription { get; set; }
    public bool HasFix => FixKeyword != null;

    public NetDoctorFinding(NetFindingSeverity severity, string title, string reason,
        string? fixKeyword = null, string? fixValue = null, string? fixDescription = null)
    {
        Severity = severity; Title = title; Reason = reason;
        FixKeyword = fixKeyword; FixValue = fixValue; FixDescription = fixDescription;
    }
}

public sealed record NetPingSummary(
    int Sent,
    int Received,
    double LossPct,
    double MinMs,
    double AvgMs,
    double MaxMs,
    double JitterMs);

public sealed record NetSnapshot(
    string AdapterGuid,
    string AdapterName,
    DateTime TakenUtc,
    Dictionary<string, string> Values);

public enum NetRevertState { Idle, Armed, Confirmed, Expired, Reverted }

public sealed class NetSettingDiff
{
    public NetSettingDiff() { }
    public NetSettingDiff(string keyword, string displayName, string from, string to)
    { Keyword = keyword; DisplayName = displayName; From = from; To = to; }
    public string Keyword { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Facts the Doctor rules evaluate. All strings are display-ready; empty means unknown.</summary>
public sealed record NetAdapterFacts(
    NetAdapterKind Kind,
    string Status,
    bool IsUp,
    long LinkSpeedBps,
    long MaxLinkSpeedBps,
    string SpeedDuplexValue,
    string SpeedDuplexDesc,
    bool EeeOn,
    bool GreenEthernetOn,
    bool PowerSavingOn,
    bool? AllowComputerToTurnOff,
    bool RssEnabled,
    string NumRssQueues,
    int ProcessorCount,
    string DriverProvider,
    string DriverDate,
    long InErrors,
    long OutErrors,
    long InDiscards,
    long OutDiscards,
    bool HasIPv6,
    List<string> Dns,
    string JumboValue,
    List<(string Name, string JumboValue)> SiblingJumbos,
    bool PcieBelowCapability,
    bool IsOnlyUpPhysical);
