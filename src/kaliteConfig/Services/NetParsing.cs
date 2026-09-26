// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>UI-free network logic: classification, categorization, validation, diffs,
/// statistics, doctor rules and the revert-timer machine. Unit-tested in tools/NetVerify.</summary>
public static class NetParsing
{
    private static readonly string[] VpnHints = { "vpn", "tap-", "tap ", "wireguard", "nordlynx", "openvpn", "ipsec", "pptp", "l2tp", "tailscale", "zerotier", "protonvpn" };
    private static readonly string[] VirtualHints = { "hyper-v", "virtualbox", "virtual ethernet", "virtual machine", "vmware", "bluetooth", "wi-fi direct", "wifi direct", "local area connection*",
        "default switch", "wsl", "vpn" };

    /// <summary>Kind from description/name. Bluetooth and Wi-Fi Direct count as Virtual;
    /// VPN matches first so a virtual VPN adapter reports VPN.</summary>
    public static NetAdapterKind ClassifyAdapter(string description, string name)
    {
        var hay = ((description ?? "") + " " + (name ?? "")).ToLowerInvariant();
        if (VpnHints.Any(h => hay.Contains(h))) return NetAdapterKind.Vpn;
        if (VirtualHints.Any(h => hay.Contains(h))) return NetAdapterKind.Virtual;
        if (hay.Contains("wi-fi") || hay.Contains("wifi") || hay.Contains("wireless") || hay.Contains("wlan")) return NetAdapterKind.WiFi;
        if (hay.Contains("ethernet") || hay.Contains("gbe") || hay.Contains("lan ")) return NetAdapterKind.Ethernet;
        return NetAdapterKind.Other;
    }

    public static bool IsHiddenByDefault(NetAdapterKind kind, string status) =>
        kind == NetAdapterKind.Virtual || kind == NetAdapterKind.Vpn;

    /// <summary>List visibility: hidden/virtual need showHidden, anything not up needs showDisconnected.</summary>
    public static bool IsListVisible(NetAdapterKind kind, bool isUp, bool showHidden, bool showDisconnected)
    {
        if ((kind == NetAdapterKind.Virtual || kind == NetAdapterKind.Vpn) && !showHidden) return false;
        if (!isUp && !showDisconnected) return false;
        return true;
    }

    /// <summary>Category purely from RegistryKeyword (never the localized display name).</summary>
    public static string CategorizeKeyword(string keyword)
    {
        var k = (keyword ?? "").ToLowerInvariant();
        if (k.Contains("speedduplex")) return "Speed & Duplex";
        if (k.Contains("flowcontrol")) return "Speed & Duplex";
        if (k.Contains("lso") || k.Contains("checksum") || k.Contains("offload") && (k.Contains("ip") || k.Contains("tcp") || k.Contains("udp"))) return "Offloads";
        if (k.Contains("rss") || k.Contains("numrss") || k.Contains("numa") || k.Contains("interrupt") || k.Contains("buffer")) return "Interrupts & Buffers";
        if (k.Contains("wake") || k.Contains("wol") || k.Contains("s5wake") || k.Contains("shutdown") && k.Contains("link")) return "Wake";
        if (k.Contains("eee") || k.Contains("greene") || k.Contains("gigalite") || k.Contains("powersaving") || k.Contains("greenethernet")) return "Power Saving";
        if (k.Contains("vlan") || k.Contains("priority") || k.Contains("qos") || k.Contains("vlanid")) return "VLAN/QoS";
        if (k.Contains("roam") || k.Contains("band") || k.Contains("vht") || k.Contains("he") || k.Contains("htmode") || k.Contains("apcompat") || k.Contains("preferredband")) return "Wi-Fi/Roaming";
        if (k.StartsWith("*")) return "Other";
        return "Vendor";
    }

    /// <summary>Plain-English, honest explanations. Performance claims are marked unproven
    /// unless a latency test measured them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Explanations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["*SpeedDuplex"] = "Link speed and duplex. Auto Negotiation is correct for almost every home setup; forcing a value only helps ancient switches, and a wrong forced value kills the link.",
            ["*FlowControl"] = "Lets the adapter ask the switch to pause briefly when its buffers fill. Usually harmless; disabling is unproven for gaming.",
            ["*InterruptModeration"] = "Batches packet interrupts to lower CPU use at high throughput. Turning it off can shave latency on fast links but raises CPU usage; effect on games is unproven without a test.",
            ["*EEE"] = "Energy-Efficient Ethernet idles the link to save power. On some switches/cables it causes dropouts or slow wake; if your link flaps, try Off.",
            ["*JumboPacket"] = "Maximum frame size. Larger than 1514 helps LAN file transfers only when EVERY device on the LAN (PC, switch, NAS) uses the same size; otherwise it fragments or drops.",
            ["*ReceiveBuffers"] = "Packets the card can queue while the CPU is busy. Raising helps 2.5G+ links under load; costs a little RAM.",
            ["*TransmitBuffers"] = "Packets queued for sending. Same tradeoff as receive buffers.",
            ["*RSS"] = "Spreads packet processing across CPU cores. Keep on for any multi-core PC; turning it off is unproven for latency.",
            ["*NumRssQueues"] = "How many CPU queues RSS may use. More is not automatically faster; match it to real load, measured.",
            ["*LsoV2IPv4"] = "Lets the card segment large TCP sends. Keep on; offloading bugs show up as slow uploads, rarely.",
            ["*TCPChecksumOffloadIPv4"] = "Card computes TCP checksums. Keep on; disable only to diagnose corruption.",
            ["*UDPChecksumOffloadIPv4"] = "Card computes UDP checksums. Keep on.",
            ["*WakeOnMagicPacket"] = "Lets a special network packet power the PC on. No effect while the PC is awake.",
            ["*WakeOnPattern"] = "Wakes on traffic patterns (ARP, etc.). Can wake the PC unexpectedly; harmless otherwise.",
            ["*PriorityVLANTag"] = "Tags packets with 802.1p priority/VLAN info. Only matters on managed networks.",
            ["EnableGreenEthernet"] = "Vendor power saving (like EEE). Same dropout caveat as EEE.",
            ["PowerSavingMode"] = "Vendor power saving for the NIC. If the link drops at idle, try Off.",
            ["NetworkAddress"] = "Overrides the card's MAC address. Blank means the burned-in address; changing it can break DHCP reservations and switch security.",
            ["RegVlanid"] = "VLAN ID for this adapter (0 = none). Only set this if your network uses VLANs.",
        };

    public static string Explain(string keyword, string displayName) =>
        Explanations.TryGetValue(keyword ?? "", out var e) ? e : $"Driver setting ({displayName}). No curated explanation; changing it is unproven for performance.";

    public static bool IsChanged(string? current, string? def) =>
        !string.Equals((current ?? "").Trim(), (def ?? "").Trim(), StringComparison.Ordinal);

    public static string OptionDesc(List<NetAdvancedOption> options, string value) =>
        options.FirstOrDefault(o => o.Value == value)?.Description ?? value;

    /// <summary>Null when valid, otherwise the human reason.</summary>
    public static string? ValidateValue(NetAdvancedProp prop, string value)
    {
        if (prop.Type.Equals("enum", StringComparison.OrdinalIgnoreCase))
        {
            if (prop.Options.Any(o => o.Value == value)) return null;
            return $"'{value}' is not one of the allowed values ({string.Join(", ", prop.Options.Select(o => o.Value))}).";
        }
        if (prop.Type.Equals("int", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(value, out var n)) return $"'{value}' is not a number.";
            if (prop.Min.HasValue && n < prop.Min.Value) return $"{n} is below the minimum {prop.Min.Value}.";
            if (prop.Max.HasValue && n > prop.Max.Value) return $"{n} is above the maximum {prop.Max.Value}.";
            if (prop.Step.HasValue && prop.Step.Value > 0 && prop.Min.HasValue && ((n - prop.Min.Value) % prop.Step.Value != 0))
                return $"{n} does not fit the step of {prop.Step.Value} from {prop.Min.Value}.";
            return null;
        }
        return null;
    }

    public static List<NetSettingDiff> BuildDiff(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        Func<string, string> displayNameOf)
    {
        var diffs = new List<NetSettingDiff>();
        foreach (var kv in after)
        {
            before.TryGetValue(kv.Key, out var from);
            if (!string.Equals(from ?? "", kv.Value ?? "", StringComparison.Ordinal))
                diffs.Add(new NetSettingDiff(kv.Key, displayNameOf(kv.Key), from ?? "(unset)", kv.Value ?? ""));
        }
        return diffs;
    }

    /// <summary>Rollback plan: every keyword whose live value differs from the snapshot.</summary>
    public static List<NetSettingDiff> RollbackPlan(
        IReadOnlyDictionary<string, string> snapshot,
        IReadOnlyDictionary<string, string> live,
        Func<string, string> displayNameOf)
        => BuildDiff(live, snapshot, displayNameOf);

    public static Dictionary<string, string> ParseNetshKeyValues(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---") || line.StartsWith("Querying") ||
                line.StartsWith("TCP ") || line.StartsWith("There is")) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            dict[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
        }
        return dict;
    }

    /// <summary>Parses `netsh interface ipv4 show offload` into per-interface
    /// capability lists. Sections look like "Interface 11: Wi-Fi" + capability lines.</summary>
    public static List<(string Iface, List<string> Caps)> ParseNetshOffload(string text)
    {
        var out_ = new List<(string, List<string>)>();
        string? current = null;
        var caps = new List<string>();
        void Flush()
        {
            if (current != null) out_.Add((current, caps));
            current = null;
            caps = new List<string>();
        }
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.StartsWith("Interface ", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var idx = line.IndexOf(':');
                current = idx > 0 ? line.Substring(idx + 1).Trim() : line;
            }
            else if (!string.IsNullOrWhiteSpace(line) && current != null)
            {
                caps.Add(line.TrimEnd('.'));
            }
        }
        Flush();
        return out_;
    }

    public static NetPingSummary PingStats(int sent, List<double> rttsMs)
    {
        var ok = rttsMs.Where(r => !double.IsNaN(r) && r >= 0).ToList();
        if (sent <= 0 || ok.Count == 0)
            return new NetPingSummary(sent, 0, sent > 0 ? 100.0 : 0, 0, 0, 0, 0);
        double jitter = 0;
        for (int i = 1; i < ok.Count; i++) jitter += Math.Abs(ok[i] - ok[i - 1]);
        if (ok.Count > 1) jitter /= (ok.Count - 1);
        return new NetPingSummary(sent, ok.Count, 100.0 * (sent - ok.Count) / sent,
            ok.Min(), ok.Average(), ok.Max(), jitter);
    }

    /// <summary>Before/after verdict. A change counts only when it exceeds the summed
    /// jitter of both runs (the measured run-to-run noise).</summary>
    public static (string Verdict, string Detail) CompareLatency(NetPingSummary before, NetPingSummary after)
    {
        if (before.Received == 0 || after.Received == 0)
            return ("unproven", "One of the runs got no replies, so nothing can be compared.");
        double noise = before.JitterMs + after.JitterMs;
        double delta = after.AvgMs - before.AvgMs;
        if (Math.Abs(delta) <= noise)
            return ("within noise", $"Average moved {delta:+0.0;-0.0} ms, inside the ±{noise:0.0} ms measured noise. Unproven either way.");
        return delta < 0
            ? ("improved", $"Average fell {-delta:0.0} ms, beyond the ±{noise:0.0} ms noise.")
            : ("regressed", $"Average rose {delta:0.0} ms, beyond the ±{noise:0.0} ms noise.");
    }

    /// <summary>Min/max-per-bucket decimation so spikes survive downsampling for graphs.</summary>
    public static List<(double Min, double Max)> DecimateMinMax(IReadOnlyList<double> samples, int buckets)
    {
        var out_ = new List<(double, double)>();
        if (samples.Count == 0 || buckets <= 0) return out_;
        int per = Math.Max(1, (int)Math.Ceiling((double)samples.Count / buckets));
        for (int i = 0; i < samples.Count; i += per)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            for (int j = i; j < Math.Min(i + per, samples.Count); j++)
            {
                mn = Math.Min(mn, samples[j]);
                mx = Math.Max(mx, samples[j]);
            }
            out_.Add((mn, mx));
        }
        return out_;
    }

    public static (string Ven, string Dev, string Subsys, string Rev) ParseVenDev(string matchingId)
    {
        var id = matchingId ?? "";
        string Get(string tag)
        {
            var i = id.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            var rest = id.Substring(i + tag.Length);
            int end = rest.IndexOfAny(new[] { '&', '\\', ' ' });
            return (end < 0 ? rest : rest.Substring(0, end)).Trim();
        }
        return (Get("VEN_"), Get("DEV_"), Get("SUBSYS_"), Get("REV_"));
    }

    /// <summary>Driver date like "3-24-2025" (or ISO). Returns years old and whether &gt; 2 years.</summary>
    public static (double Years, bool OlderThanTwoYears) DriverAge(string? dateStr, DateTime utcNow)
    {
        if (DateTime.TryParse(dateStr, out var d) || DateTime.TryParseExact(dateStr, "M-d-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out d))
        {
            double years = (utcNow - d.ToUniversalTime()).TotalDays / 365.25;
            return (years, years > 2.0);
        }
        return (0, false);
    }

    public static string FormatSpeed(long bps)
    {
        if (bps <= 0) return "0 bps";
        double g = bps / 1e9;
        if (g >= 1) return g % 1 == 0 ? $"{g:0} Gbps" : $"{g:0.#} Gbps";
        double m = bps / 1e6;
        return m % 1 == 0 ? $"{m:0} Mbps" : $"{m:0.#} Mbps";
    }

    public static DateTime LeaseToLocal(long unixSeconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime; }
        catch { return DateTime.MinValue; }
    }

    public static NetRevertState RevertAdvance(NetRevertState state, string evt)
    {
        return (state, evt) switch
        {
            (NetRevertState.Idle, "arm") => NetRevertState.Armed,
            (NetRevertState.Armed, "confirm") => NetRevertState.Confirmed,
            (NetRevertState.Armed, "timeout") => NetRevertState.Expired,
            (NetRevertState.Armed, "linklost") => NetRevertState.Expired,
            (NetRevertState.Expired, "revert") => NetRevertState.Reverted,
            (NetRevertState.Reverted, "arm") => NetRevertState.Armed,
            (NetRevertState.Confirmed, "arm") => NetRevertState.Armed,
            _ => state,
        };
    }

    /// <summary>Doctor rules over real facts. No measurement behind a claim -&gt; Info + "unproven".</summary>
    public static List<NetDoctorFinding> DoctorRules(NetAdapterFacts f)
    {
        var out_ = new List<NetDoctorFinding>();
        if (f.Kind != NetAdapterKind.Virtual && f.Kind != NetAdapterKind.Vpn)
        {
            if (f.IsUp && f.MaxLinkSpeedBps > 0 && f.LinkSpeedBps > 0 && f.LinkSpeedBps < f.MaxLinkSpeedBps)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    $"Negotiated {FormatSpeed(f.LinkSpeedBps)} below the NIC's {FormatSpeed(f.MaxLinkSpeedBps)}",
                    "Link trains lower when the cable, port or a forced setting caps it. Check the cable and keep Speed & Duplex on Auto Negotiation.",
                    "*SpeedDuplex", "0", "Set Speed & Duplex back to Auto Negotiation"));
            if (f.SpeedDuplexValue != "0" && !string.IsNullOrEmpty(f.SpeedDuplexValue))
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    $"Speed & Duplex is forced ({f.SpeedDuplexDesc})",
                    "Forced speed/duplex mismatches silently halve throughput or drop the link on modern switches. Auto is correct unless a device demands otherwise.",
                    "*SpeedDuplex", "0", "Set Speed & Duplex back to Auto Negotiation"));
            if (f.EeeOn || f.GreenEthernetOn)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Info,
                    "Energy-saving Ethernet features are on",
                    "EEE / Green Ethernet idles the link to save power. On some cables it causes dropouts; the effect on latency is unproven without a test.",
                    "*EEE", "0", "Turn EEE off"));
            if (f.PowerSavingOn)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "Vendor power saving is on",
                    "Power-saving modes can idle or reset the NIC and look like random dropouts.",
                    "PowerSavingMode", "0", "Turn Power Saving Mode off"));
            if (f.AllowComputerToTurnOff == true)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "Windows may turn this device off to save power",
                    "Sleep/idle power-off is a classic cause of 'Wi-Fi died overnight' and USB-NIC dropouts."));
            if (!f.RssEnabled && f.ProcessorCount > 2 && (f.Kind == NetAdapterKind.Ethernet))
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "Receive Side Scaling is off on a multi-core PC",
                    "Without RSS every packet is processed on one core, which caps fast links.",
                    "*RSS", "1", "Turn RSS on"));
            if (!string.IsNullOrEmpty(f.DriverProvider) &&
                f.DriverProvider.IndexOf("microsoft", StringComparison.OrdinalIgnoreCase) >= 0 &&
                f.Kind == NetAdapterKind.Ethernet)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Info,
                    "Generic Microsoft driver in use",
                    "The inbox driver works but hides vendor features and older tuning. A vendor driver usually exposes more settings."));
            var (years, old) = DriverAge(f.DriverDate, DateTime.UtcNow);
            if (old)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Info,
                    $"Driver is {years:0} years old ({f.DriverDate})",
                    "Old drivers miss bug fixes; update from the vendor only if something misbehaves."));
            if (f.PcieBelowCapability)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "PCIe link below the device's capability",
                    "The NIC renegotiated to fewer lanes or an older PCIe generation (slot choice, BIOS or power saving). Reseat/check the slot."));
            if (f.InErrors + f.OutErrors + f.InDiscards + f.OutDiscards > 100 && f.IsUp)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    $"High error/discard counters (in-err {f.InErrors}, out-err {f.OutErrors}, discards {f.InDiscards + f.OutDiscards})",
                    "Climbing counters mean cable, duplex mismatch or a dying port. Note the numbers, wait, and compare."));
            if (f.SiblingJumbos.Select(s => s.JumboValue).Distinct().Count() > 1)
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "Jumbo frames differ between adapters",
                    "Mixed MTUs fragment or blackhole LAN traffic. Use one size everywhere or leave all at 1514."));
            if (!f.HasIPv6 && f.Dns.Count > 0 && f.Dns.All(d => d.Contains(":")))
                out_.Add(new NetDoctorFinding(NetFindingSeverity.Warning,
                    "Only IPv6 DNS servers but no IPv6 address",
                    "Name resolution can stall waiting on unreachable IPv6 resolvers. Add an IPv4 DNS or remove the IPv6 ones."));
        }
        return out_;
    }

    // ------------------------------------------------- system-wide editable rows

    public enum SysKind { Enum, Number }

    /// <summary>One editable system-wide row. Setter describes HOW it writes:
    /// NetshTcp/NetshUdp/NetshSupplemental/PsOffload/PsRss with the exact tag.</summary>
    public sealed record SysRowDef(
        string Section, string Key, string Label, SysKind Kind,
        List<string> Options, long Min, long Max,
        string Setter, string SetterArg);

    private static readonly List<string> OffOnDefault = new() { "Enabled", "Disabled", "Default" };

    /// <summary>Every row below was verified settable on this machine (netsh set ? help
    /// plus -WhatIf probes for the CIM cmdlets). No row without a proven setter.</summary>
    public static List<SysRowDef> SysRowDefs() => new()
    {
        // TCP globals: netsh interface tcp set global <tag>=<value>
        new SysRowDef("TCP Global Parameters", "Receive-Side Scaling State", "Receive-Side Scaling State",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "rss"),
        new SysRowDef("TCP Global Parameters", "Receive Window Auto-Tuning Level", "Receive Window Auto-Tuning Level",
            SysKind.Enum, new List<string> { "Disabled", "HighlyRestricted", "Restricted", "Normal", "Experimental" }, 0, 0, "NetshTcp", "autotuninglevel"),
        new SysRowDef("TCP Global Parameters", "Add-On Congestion Control Provider", "Add-On Congestion Control Provider",
            SysKind.Enum, new List<string> { "None", "Ctcp", "Dctcp", "Cubic", "Bbr2", "Default" }, 0, 0, "NetshSupplemental", "congestionprovider"),
        new SysRowDef("TCP Global Parameters", "ECN Capability", "ECN Capability",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "ecncapability"),
        new SysRowDef("TCP Global Parameters", "RFC 1323 Timestamps", "RFC 1323 Timestamps",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "timestamps"),
        new SysRowDef("TCP Global Parameters", "Initial RTO", "Initial RTO",
            SysKind.Number, new List<string>(), 300, 3000, "NetshTcp", "initialrto"),
        new SysRowDef("TCP Global Parameters", "Receive Segment Coalescing State", "Receive Segment Coalescing State",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "rsc"),
        new SysRowDef("TCP Global Parameters", "Non Sack Rtt Resiliency", "Non Sack Rtt Resiliency",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "nonsackrttresiliency"),
        new SysRowDef("TCP Global Parameters", "Max SYN Retransmissions", "Max SYN Retransmissions",
            SysKind.Number, new List<string>(), 2, 8, "NetshTcp", "maxsynretransmissions"),
        new SysRowDef("TCP Global Parameters", "Fast Open", "Fast Open",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "fastopen"),
        new SysRowDef("TCP Global Parameters", "Fast Open Fallback", "Fast Open Fallback",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "fastopenfallback"),
        new SysRowDef("TCP Global Parameters", "HyStart", "HyStart",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "hystart"),
        new SysRowDef("TCP Global Parameters", "Proportional Rate Reduction", "Proportional Rate Reduction",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshTcp", "prr"),
        new SysRowDef("TCP Global Parameters", "Pacing Profile", "Pacing Profile",
            SysKind.Enum, new List<string> { "Off", "InitialWindow", "SlowStart", "Always", "Default" }, 0, 0, "NetshTcp", "pacingprofile"),
        // UDP globals: netsh interface udp set global uro=/uso=
        new SysRowDef("UDP Global Parameters", "Receive Offload State", "Receive Offload State",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshUdp", "uro"),
        new SysRowDef("UDP Global Parameters", "Send Offload State", "Send Offload State",
            SysKind.Enum, new List<string>(OffOnDefault), 0, 0, "NetshUdp", "uso"),
        // Offload globals: Set-NetOffloadGlobalSetting -<Property> <Enabled|Disabled|...>
        new SysRowDef("Global Offload Settings", "ReceiveSideScaling", "ReceiveSideScaling",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "ReceiveSideScaling"),
        new SysRowDef("Global Offload Settings", "ReceiveSegmentCoalescing", "ReceiveSegmentCoalescing",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "ReceiveSegmentCoalescing"),
        new SysRowDef("Global Offload Settings", "Chimney", "Chimney",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "Chimney"),
        new SysRowDef("Global Offload Settings", "TaskOffload", "TaskOffload",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "TaskOffload"),
        new SysRowDef("Global Offload Settings", "NetworkDirect", "NetworkDirect",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "NetworkDirect"),
        new SysRowDef("Global Offload Settings", "NetworkDirectAcrossIPSubnets", "NetworkDirectAcrossIPSubnets",
            SysKind.Enum, new List<string> { "Blocked", "Allowed" }, 0, 0, "PsOffload", "NetworkDirectAcrossIPSubnets"),
        new SysRowDef("Global Offload Settings", "PacketCoalescingFilter", "PacketCoalescingFilter",
            SysKind.Enum, new List<string> { "Enabled", "Disabled" }, 0, 0, "PsOffload", "PacketCoalescingFilter"),
        // Per-adapter RSS: Set-NetAdapterRss -Name X -<Property> <value>
        new SysRowDef("Receive Side Scaling", "RSS Profile", "RSS Profile",
            SysKind.Enum, new List<string> { "Closest", "ClosestStatic", "NUMA", "NUMAStatic", "Conservative", "Balanced" }, 0, 0, "PsRss", "Profile"),
        new SysRowDef("Receive Side Scaling", "RSS Queues", "RSS Queues",
            SysKind.Number, new List<string>(), 1, 64, "PsRss", "NumberOfReceiveQueues"),
        new SysRowDef("Receive Side Scaling", "RSS Base Processor", "RSS Base Processor",
            SysKind.Number, new List<string>(), 0, 63, "PsRss", "BaseProcessorNumber"),
        new SysRowDef("Receive Side Scaling", "RSS Max Processor", "RSS Max Processor",
            SysKind.Number, new List<string>(), 0, 63, "PsRss", "MaxProcessors"),
    };

    /// <summary>What each section actually covers, shown above its rows.</summary>
    private static readonly IReadOnlyDictionary<string, string> SysSectionHints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TCP Global Parameters"] = "Machine-wide TCP stack defaults, shared by every adapter on this PC.",
            ["UDP Global Parameters"] = "Machine-wide UDP offload defaults.",
            ["Global Offload Settings"] = "Offload engine switches for the whole system. No netsh equivalent exists for these, so they go through PowerShell.",
            ["Receive Side Scaling"] = "Per-adapter RSS settings, applied to the adapter selected on the Overview tab.",
        };

    public static string SysSectionHint(string section) =>
        SysSectionHints.TryGetValue(section ?? "", out var hint) ? hint : "System-wide network settings.";

    /// <summary>RSS profile int (as CIM JSON reports it) to setter name. Measured live:
    /// 1=Closest 2=ClosestStatic 3=NUMA 4=NUMAStatic 5=Conservative 6=Balanced.</summary>
    public static string RssProfileName(int v) => v switch
    {
        1 => "Closest",
        2 => "ClosestStatic",
        3 => "NUMA",
        4 => "NUMAStatic",
        5 => "Conservative",
        6 => "Balanced",
        _ => $"Unknown ({v})",
    };

    public static string OffloadDisplay(string property, int v) => property switch
    {
        "NetworkDirectAcrossIPSubnets" => v == 1 ? "Allowed" : "Blocked",
        _ => v == 1 ? "Enabled" : "Disabled",
    };

    /// <summary>Builds the exact setter command. Pure and unit-tested; execution lives in NetSysService.</summary>
    public static (string Tool, string Args) SysSetterCommand(SysRowDef row, string adapterName, string value)
        => row.Setter switch
        {
            "NetshTcp" => ("netsh", $"interface tcp set global {row.SetterArg}={value}"),
            "NetshUdp" => ("netsh", $"interface udp set global {row.SetterArg}={value}"),
            "NetshSupplemental" => ("netsh", $"interface tcp set supplemental template=internet {row.SetterArg}={value}"),
            "PsOffload" => ("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Set-NetOffloadGlobalSetting -{row.SetterArg} {value} -Confirm:$false\""),
            "PsRss" => ("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Set-NetAdapterRss -Name '{adapterName.Replace("'", "''")}' -{row.SetterArg} {value} -Confirm:$false\""),
            _ => throw new ArgumentException("Unknown setter " + row.Setter),
        };

    /// <summary>Live spellings that mean one of a row's options but print differently.
    /// netsh shows the default RFC 1323 state as "allowed".</summary>
    private static readonly IReadOnlyDictionary<string, string> SysValueAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RFC 1323 Timestamps|allowed"] = "Default",
        };

    /// <summary>Maps a live value onto the row's own option spelling. Windows reports
    /// "enabled"/"normal"/"off" while the options list is capitalized, and a combo bound
    /// by SelectedValue shows nothing when the two differ. Values matching no option are
    /// returned trimmed and unchanged.</summary>
    public static string CanonicalOption(SysRowDef row, string? value)
    {
        var v = (value ?? "").Trim().TrimEnd('.');
        if (SysValueAliases.TryGetValue($"{row.Key}|{v}", out var alias)) return alias;
        return row.Options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)) ?? v;
    }

    public static string? ValidateSysValue(SysRowDef row, string value)
    {
        if (row.Kind == SysKind.Enum)
            return row.Options.Any(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase))
                ? null : $"'{value}' is not allowed ({string.Join("/", row.Options)}).";
        if (!long.TryParse(value, out var n)) return $"'{value}' is not a number.";
        if (n < row.Min || n > row.Max) return $"{n} is outside {row.Min}–{row.Max}.";
        return null;
    }
}
