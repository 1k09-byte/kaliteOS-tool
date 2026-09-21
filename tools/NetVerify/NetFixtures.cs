using System;
using System.Collections.Generic;
using kaliteConfig.Services;

namespace NetVerify;

/// <summary>Recorded + synthetic fixtures. Realtek props were read live from this machine
/// (Realtek PCIe 2.5GbE, key 0010) on 2026-09-21; the Intel set is synthetic and labeled so.</summary>
internal static class NetFixtures
{
    public static List<(string Keyword, string Display, string Type, string Current, string Default,
        List<(string V, string D)> Options, long? Min, long? Max, long? Step)> Realtek() => new()
    {
        ("*SpeedDuplex", "Speed & Duplex", "enum", "0", "0",
            new() { ("0", "Auto Negotiation"), ("1", "10 Mbps Half Duplex"), ("2", "10 Mbps Full Duplex"), ("3", "100 Mbps Half Duplex"), ("4", "100 Mbps Full Duplex"), ("6", "1.0 Gbps Full Duplex"), ("2500", "2.5 Gbps Full Duplex") }, null, null, null),
        ("*FlowControl", "Flow Control", "enum", "3", "3",
            new() { ("0", "Disabled"), ("1", "Rx Enabled"), ("2", "Tx Enabled"), ("3", "Rx & Tx Enabled") }, null, null, null),
        ("*JumboPacket", "Jumbo Frame", "enum", "1514", "1514",
            new() { ("1514", "Disabled"), ("4088", "4KB MTU"), ("9014", "9KB MTU") }, null, null, null),
        ("*ReceiveBuffers", "Receive Buffers", "int", "1024", "1024", new(), 32, 4096, 8),
        ("*TransmitBuffers", "Transmit Buffers", "int", "512", "512", new(), 32, 2048, 8),
        ("*RSS", "Receive Side Scaling", "enum", "1", "1", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
        ("*NumRssQueues", "Maximum Number of RSS Queues", "enum", "4", "4",
            new() { ("1", "1 Queue"), ("2", "2 Queues"), ("4", "4 Queues"), ("8", "8 Queues") }, null, null, null),
        ("*EEE", "Energy-Efficient Ethernet", "enum", "1", "1", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
        ("EnableGreenEthernet", "Green Ethernet", "enum", "1", "1", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
        ("PowerSavingMode", "Power Saving Mode", "enum", "1", "1", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
        ("RegVlanid", "VLAN ID", "int", "0", "0", new(), 0, 4094, 1),
        ("NetworkAddress", "Network Address", "edit", "", "", new(), null, null, null),
    };

    public static List<(string Keyword, string Display, string Type, string Current, string Default,
        List<(string V, string D)> Options, long? Min, long? Max, long? Step)> WifiReal() => new()
    {
        ("PreferredBand", "Preferred Band", "enum", "2", "2",
            new() { ("0", "No Preference"), ("1", "Prefer 2.4GHz"), ("2", "Prefer 5GHz"), ("3", "Prefer 6GHz") }, null, null, null),
        ("RoamNeedIndicateTh", "Roaming Aggressiveness", "enum", "3", "3",
            new() { ("1", "Lowest"), ("2", "Medium-low"), ("3", "Medium"), ("4", "Medium-High"), ("5", "Highest") }, null, null, null),
        ("TxPowerLevel", "Transmit Power", "enum", "5", "5",
            new() { ("1", "1. Lowest"), ("5", "5. Highest") }, null, null, null),
        ("*WakeOnMagicPacket", "Wake on Magic Packet", "enum", "1", "0", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
    };

    /// <summary>Synthetic minimal Intel-style set (no Intel NIC on this machine).</summary>
    public static List<(string Keyword, string Display, string Type, string Current, string Default,
        List<(string V, string D)> Options, long? Min, long? Max, long? Step)> IntelSynthetic() => new()
    {
        ("*SpeedDuplex", "Speed & Duplex", "enum", "0", "0",
            new() { ("0", "Auto Negotiation"), ("6", "1.0 Gbps Full Duplex") }, null, null, null),
        ("*InterruptModeration", "Interrupt Moderation", "enum", "1", "1", new() { ("0", "Disabled"), ("1", "Enabled") }, null, null, null),
        ("*ReceiveBuffers", "Receive Buffers", "int", "256", "256", new(), 80, 2048, 8),
    };

    public static NetAdapterFacts RealtekFacts() => new(
        Kind: NetAdapterKind.Ethernet, Status: "Disconnected", IsUp: false,
        LinkSpeedBps: 0, MaxLinkSpeedBps: 2500000000,
        SpeedDuplexValue: "0", SpeedDuplexDesc: "Auto Negotiation",
        EeeOn: true, GreenEthernetOn: true, PowerSavingOn: true, AllowComputerToTurnOff: true,
        RssEnabled: true, NumRssQueues: "4", ProcessorCount: 16,
        DriverProvider: "Realtek", DriverDate: "3-24-2025",
        InErrors: 0, OutErrors: 0, InDiscards: 0, OutDiscards: 0,
        HasIPv6: false, Dns: new List<string>(),
        JumboValue: "1514",
        SiblingJumbos: new List<(string, string)> { ("Ethernet 2", "1514") },
        PcieBelowCapability: false, IsOnlyUpPhysical: false);

    public const string NetshTcpSample = @"Querying active state...

TCP Global Parameters
----------------------------------------------
Receive-Side Scaling State          : enabled
Receive Window Auto-Tuning Level    : normal
Add-On Congestion Control Provider  : default
ECN Capability                      : disabled
RFC 1323 Timestamps                 : allowed
Initial RTO                         : 1000
Receive Segment Coalescing State    : enabled
";

    public const string NetshWlanSample = @"
    Name                   : Wi-Fi
    State                  : connected
    SSID                   : Sneakbomovements5
    AP BSSID               : 80:23:95:11:b5:e1
    Band                   : 5 GHz
    Channel                : 36
    Radio type             : 802.11ax
    Authentication         : WPA2-Personal
    Cipher                 : CCMP
    Receive rate (Mbps)    : 1201
    Transmit rate (Mbps)   : 1201
    Signal                 : 77%
    Rssi                   : -64
";

    public const string NetshOffloadSample = @"
Interface 20: Ethernet 2

ipv4 transmit checksum supported.
udp transmit checksum supported.
tcp transmit checksum supported.
tcp giant send offload supported.
ipv4 receive checksum supported.
udp receive checksum supported.
tcp receive checksum supported.

Interface 22: Ethernet 3

Interface 11: Wi-Fi

tcp large send offload supported.
tcp giant send offload supported.
";

    public const string NetshSupplementalSample = @"
The TCP global default template is internet

TCP Supplemental Parameters
----------------------------------------------
Minimum RTO (msec)                  : 300
Initial Congestion Window (MSS)     : 10
Congestion Control Provider         : cubic
Enable Congestion Window Restart    : disabled
Delayed ACK timeout (msec)          : 40
Delayed ACK frequency               : 2
Enable RACK                         : enabled
Enable Tail Loss Probe              : enabled
";

    public const string OffloadJsonSample = @"{""ReceiveSideScaling"":1,""ReceiveSegmentCoalescing"":1,""TaskOffload"":1,""NetworkDirect"":1,""NetworkDirectAcrossIPSubnets"":0,""PacketCoalescingFilter"":1,""Chimney"":0}";

    public const string RssJsonSample = @"{""Profile"":4,""BaseProcessorNumber"":0,""MaxProcessorNumber"":14,""MaxProcessors"":8,""NumberOfReceiveQueues"":4}";
}
