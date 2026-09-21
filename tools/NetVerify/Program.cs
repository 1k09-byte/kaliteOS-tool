using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Services;

namespace NetVerify;

internal sealed class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string label)
    {
        if (cond) { _pass++; Console.WriteLine($"PASS  {label}"); }
        else { _fail++; Console.WriteLine($"FAIL  {label}"); }
    }

    private static NetAdvancedProp Mk((string Keyword, string Display, string Type, string Current, string Default,
        List<(string V, string D)> Options, long? Min, long? Max, long? Step) t)
    {
        return new NetAdvancedProp
        {
            Keyword = t.Keyword,
            DisplayName = t.Display,
            Type = t.Type,
            Current = t.Current,
            Default = t.Default,
            Options = t.Options.Select(o => new NetAdvancedOption(o.V, o.D)).ToList(),
            Min = t.Min,
            Max = t.Max,
            Step = t.Step,
            Category = NetParsing.CategorizeKeyword(t.Keyword),
            Changed = NetParsing.IsChanged(t.Current, t.Default),
            Explanation = NetParsing.Explain(t.Keyword, t.Display),
        };
    }

    private static async System.Threading.Tasks.Task<int> Main()
    {
        // Classification (recorded descriptions from this machine)
        Check(NetParsing.ClassifyAdapter("Realtek PCIe 2.5GbE Family Controller", "Ethernet 2") == NetAdapterKind.Ethernet, "classify Realtek Ethernet");
        Check(NetParsing.ClassifyAdapter("MediaTek Wi-Fi 7 MT7925 Wireless LAN Card", "Wi-Fi") == NetAdapterKind.WiFi, "classify MediaTek WiFi");
        Check(NetParsing.ClassifyAdapter("Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)") == NetAdapterKind.Virtual, "classify Hyper-V virtual");
        Check(NetParsing.ClassifyAdapter("VirtualBox Host-Only Ethernet Adapter", "Ethernet 3") == NetAdapterKind.Virtual, "classify VirtualBox virtual");
        Check(NetParsing.ClassifyAdapter("TAP-Windows Adapter V9", "Ethernet 4") == NetAdapterKind.Vpn, "classify TAP VPN");
        Check(NetParsing.ClassifyAdapter("Bluetooth Device (Personal Area Network)", "Bluetooth") == NetAdapterKind.Virtual, "classify Bluetooth virtual");
        Check(NetParsing.ClassifyAdapter("Microsoft Wi-Fi Direct Virtual Adapter", "Local Area Connection* 2") == NetAdapterKind.Virtual, "classify Wi-Fi Direct virtual");
        Check(NetParsing.IsHiddenByDefault(NetAdapterKind.Virtual, "Up"), "hide virtual by default");
        Check(NetParsing.IsHiddenByDefault(NetAdapterKind.Vpn, "Up"), "hide VPN by default");
        Check(!NetParsing.IsHiddenByDefault(NetAdapterKind.Ethernet, "Up"), "show Ethernet by default");
        Check(NetParsing.IsListVisible(NetAdapterKind.Ethernet, true, false, false), "list shows connected Ethernet");
        Check(!NetParsing.IsListVisible(NetAdapterKind.Ethernet, false, false, false), "list hides disconnected by default");
        Check(NetParsing.IsListVisible(NetAdapterKind.Ethernet, false, false, true), "list shows disconnected on toggle");
        Check(!NetParsing.IsListVisible(NetAdapterKind.Virtual, true, false, true), "list hides virtual without toggle");
        Check(NetParsing.IsListVisible(NetAdapterKind.Virtual, true, true, false), "list shows virtual on toggle");

        // Categorization incl. vendor keywords
        Check(NetParsing.CategorizeKeyword("*SpeedDuplex") == "Speed & Duplex", "cat SpeedDuplex");
        Check(NetParsing.CategorizeKeyword("*LsoV2IPv4") == "Offloads", "cat LSO");
        Check(NetParsing.CategorizeKeyword("*TCPChecksumOffloadIPv4") == "Offloads", "cat checksum");
        Check(NetParsing.CategorizeKeyword("*RSS") == "Interrupts & Buffers", "cat RSS");
        Check(NetParsing.CategorizeKeyword("*ReceiveBuffers") == "Interrupts & Buffers", "cat buffers");
        Check(NetParsing.CategorizeKeyword("*WakeOnMagicPacket") == "Wake", "cat WoL");
        Check(NetParsing.CategorizeKeyword("S5WakeOnLan") == "Wake", "cat S5 WoL");
        Check(NetParsing.CategorizeKeyword("*EEE") == "Power Saving", "cat EEE");
        Check(NetParsing.CategorizeKeyword("EnableGreenEthernet") == "Power Saving", "cat GreenEthernet");
        Check(NetParsing.CategorizeKeyword("*PriorityVLANTag") == "VLAN/QoS", "cat VLAN");
        Check(NetParsing.CategorizeKeyword("RegVlanid") == "VLAN/QoS", "cat RegVlanid");
        Check(NetParsing.CategorizeKeyword("PreferredBand") == "Wi-Fi/Roaming", "cat PreferredBand");
        Check(NetParsing.CategorizeKeyword("RoamNeedIndicateTh") == "Wi-Fi/Roaming", "cat roam");
        Check(NetParsing.CategorizeKeyword("*UnknownNewThing") == "Other", "cat unknown star");
        Check(NetParsing.CategorizeKeyword("GigaLite") == "Power Saving", "cat GigaLite vendor power");
        Check(NetParsing.CategorizeKeyword("SomeVendorX") == "Vendor", "cat bare vendor keyword");

        // Changed detection + option text
        Check(!NetParsing.IsChanged("0", "0"), "unchanged equal");
        Check(NetParsing.IsChanged("1", "0"), "changed differs");
        Check(!NetParsing.IsChanged("", ""), "unchanged both empty");
        var speed = Mk(NetFixtures.Realtek()[0]);
        Check(NetParsing.OptionDesc(speed.Options, "0") == "Auto Negotiation", "option desc lookup");
        Check(NetParsing.OptionDesc(speed.Options, "99") == "99", "option desc falls back to value");
        Check(!speed.Changed, "recorded SpeedDuplex not changed");
        var wol = Mk(NetFixtures.WifiReal()[3]);
        Check(wol.Changed, "recorded WoL differs from default");

        // Validation
        Check(NetParsing.ValidateValue(speed, "6") is null, "validate enum ok");
        Check(NetParsing.ValidateValue(speed, "99") is not null, "validate enum rejects");
        var rx = Mk(NetFixtures.Realtek()[3]);
        Check(NetParsing.ValidateValue(rx, "2048") is null, "validate int ok");
        Check(NetParsing.ValidateValue(rx, "5000") is not null, "validate int over max");
        Check(NetParsing.ValidateValue(rx, "100") is not null, "validate int breaks step");
        Check(NetParsing.ValidateValue(rx, "abc") is not null, "validate int rejects text");
        var vlan = Mk(NetFixtures.Realtek()[10]);
        Check(NetParsing.ValidateValue(vlan, "100") is null, "validate vlan ok");

        // Diff + rollback plan
        var before = new Dictionary<string, string> { ["*EEE"] = "1", ["*RSS"] = "1" };
        var after = new Dictionary<string, string> { ["*EEE"] = "0", ["*RSS"] = "1" };
        var diffs = NetParsing.BuildDiff(before, after, k => k);
        Check(diffs.Count == 1 && diffs[0].Keyword == "*EEE" && diffs[0].From == "1" && diffs[0].To == "0", "diff one change");
        var plan = NetParsing.RollbackPlan(new Dictionary<string, string> { ["*EEE"] = "1" },
            new Dictionary<string, string> { ["*EEE"] = "0" }, k => k);
        Check(plan.Count == 1 && plan[0].To == "1", "rollback restores snapshot value");

        // netsh parsing on recorded output
        var tcp = NetParsing.ParseNetshKeyValues(NetFixtures.NetshTcpSample);
        Check(tcp["Receive Window Auto-Tuning Level"] == "normal", "parse autotuning");
        Check(tcp["Initial RTO"] == "1000", "parse RTO");
        Check(tcp["ECN Capability"] == "disabled", "parse ECN");
        var wlan = NetParsing.ParseNetshKeyValues(NetFixtures.NetshWlanSample);
        Check(wlan["SSID"] == "Sneakbomovements5", "parse SSID");
        Check(wlan["AP BSSID"] == "80:23:95:11:b5:e1", "parse BSSID");
        Check(wlan["Signal"] == "77%", "parse signal");

        // Offload blocks (recorded netsh interface ipv4 show offload)
        var off = NetParsing.ParseNetshOffload(NetFixtures.NetshOffloadSample);
        var eth2 = off.FirstOrDefault(o => o.Iface == "Ethernet 2");
        Check(eth2.Caps.Count == 7 && eth2.Caps.Contains("tcp giant send offload supported"), "parse offload block");
        var eth3 = off.FirstOrDefault(o => o.Iface == "Ethernet 3");
        Check(eth3.Caps.Count == 0, "parse empty offload block");
        Check(off.Any(o => o.Iface == "Wi-Fi"), "parse wifi offload block");

        // Ping math
        var ps = NetParsing.PingStats(20, new List<double> { 1, 2, 3, 4 });
        Check(ps.Received == 4 && ps.LossPct == 80.0 && Math.Abs(ps.AvgMs - 2.5) < 1e-9, "ping loss+avg");
        Check(Math.Abs(ps.JitterMs - 1.0) < 1e-9, "ping jitter mean abs diff");
        var ps0 = NetParsing.PingStats(4, new List<double>());
        Check(ps0.LossPct == 100.0 && ps0.Received == 0, "ping total loss");
        var (v1, _) = NetParsing.CompareLatency(
            new NetPingSummary(20, 20, 0, 1, 10, 20, 2),
            new NetPingSummary(20, 20, 0, 1, 4, 20, 2));
        Check(v1 == "improved", "latency improved beyond noise");
        var (v2, _) = NetParsing.CompareLatency(
            new NetPingSummary(20, 20, 0, 1, 10, 20, 2),
            new NetPingSummary(20, 20, 0, 1, 11, 20, 2));
        Check(v2 == "within noise", "latency within noise");
        var (v3, _) = NetParsing.CompareLatency(
            new NetPingSummary(20, 0, 100, 0, 0, 0, 0),
            new NetPingSummary(20, 20, 0, 1, 4, 20, 2));
        Check(v3 == "unproven", "latency unproven on total loss");

        // Decimation keeps spikes
        var dec = NetParsing.DecimateMinMax(new List<double> { 1, 100, 2, 3, 4, 5 }, 3);
        Check(dec.Count == 3 && dec[0].Max == 100 && dec[0].Min == 1, "decimation keeps spike");

        // VEN/DEV parse on the recorded Realtek ID
        var (ven, dev, subsys, rev) = NetParsing.ParseVenDev(@"PCI\VEN_10EC&DEV_8125&SUBSYS_E0001458&REV_0C");
        Check(ven == "10EC" && dev == "8125" && subsys == "E0001458" && rev == "0C", "parse VEN/DEV/SUBSYS/REV");

        // Driver age + speed + lease
        var (years, old) = NetParsing.DriverAge("3-24-2025", new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
        Check(!old && years > 1.0 && years < 2.0, "driver age ~1.5y not flagged");
        var (_, old2) = NetParsing.DriverAge("1-01-2020", new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
        Check(old2, "driver age 6y flagged");
        var (_, old3) = NetParsing.DriverAge("not a date", DateTime.UtcNow);
        Check(!old3, "driver age unparseable not flagged");
        Check(NetParsing.FormatSpeed(2500000000) == "2.5 Gbps", "format 2.5G");
        Check(NetParsing.FormatSpeed(1200000000) == "1.2 Gbps", "format 1.2G");
        Check(NetParsing.FormatSpeed(0) == "0 bps", "format zero");
        var lease = NetParsing.LeaseToLocal(1789981934);
        Check(lease.Year == 2026, "lease epoch converts");

        // Revert machine
        Check(NetParsing.RevertAdvance(NetRevertState.Idle, "arm") == NetRevertState.Armed, "revert arm");
        Check(NetParsing.RevertAdvance(NetRevertState.Armed, "confirm") == NetRevertState.Confirmed, "revert confirm");
        Check(NetParsing.RevertAdvance(NetRevertState.Armed, "timeout") == NetRevertState.Expired, "revert timeout");
        Check(NetParsing.RevertAdvance(NetRevertState.Expired, "revert") == NetRevertState.Reverted, "revert executes");
        Check(NetParsing.RevertAdvance(NetRevertState.Idle, "confirm") == NetRevertState.Idle, "revert ignores stray confirm");

        // Doctor on the recorded Realtek facts
        var findings = NetParsing.DoctorRules(NetFixtures.RealtekFacts());
        Check(findings.Any(f => f.Title.Contains("power saving")), "doctor flags PowerSavingMode");
        Check(findings.Any(f => f.Title.Contains("turn this device off") && f.Severity == NetFindingSeverity.Warning), "doctor flags allow-off");
        Check(findings.Any(f => f.Title.Contains("Energy-saving") && f.Severity == NetFindingSeverity.Info), "doctor EEE is info, unproven");
        Check(!findings.Any(f => f.Title.Contains("forced")), "doctor quiet on auto duplex");
        Check(!findings.Any(f => f.Title.Contains("generic Microsoft")), "doctor quiet on vendor driver");

        var forced = NetFixtures.RealtekFacts() with { IsUp = true, LinkSpeedBps = 100000000, SpeedDuplexValue = "4", SpeedDuplexDesc = "100 Mbps Full Duplex", MaxLinkSpeedBps = 2500000000 };
        var ff = NetParsing.DoctorRules(forced);
        Check(ff.Any(f => f.Title.Contains("below the NIC") && f.FixKeyword == "*SpeedDuplex"), "doctor flags under-negotiation with fix");
        Check(ff.Any(f => f.Title.Contains("forced")), "doctor flags forced duplex");

        var mixed = NetFixtures.RealtekFacts() with
        {
            SiblingJumbos = new List<(string, string)> { ("Ethernet 2", "1514"), ("Wi-Fi", "9014") }
        };
        Check(NetParsing.DoctorRules(mixed).Any(f => f.Title.Contains("Jumbo")), "doctor flags mixed jumbo");

        Console.WriteLine();
        Console.WriteLine("--- speed formatting ---");
        SpeedTests.Run(Check);

        Console.WriteLine();
        Console.WriteLine("--- system rows ---");
        SysRowTests.Run(Check);

        Console.WriteLine();
        Console.WriteLine("--- live read smoke ---");
        await NetReadSmoke.RunAsync(Check);

        Console.WriteLine();
        Console.WriteLine($"NetVerify: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
