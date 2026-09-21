using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>Reads every network adapter and its driver settings.
/// Mechanism: managed System.Net.NetworkInformation for link/IP/stats, the driver
/// class registry key (HKLM\...\Control\Class\{4d36e972...}\NNNN, matched by
/// NetCfgInstanceId) for everything the driver exposes, and awaited netsh for
/// TCP-global and Wi-Fi state. No CIM/WMI, no PowerShell SDK, no orphan processes:
/// registry and NetworkInformation are in-process; netsh goes through NetProcess
/// with a timeout and kill switch. All reads are async off the UI thread.</summary>
public static class NetAdapterService
{
    public const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static string ClassBase =>
        $@"SYSTEM\CurrentControlSet\Control\Class\{NetClassGuid}";

    public static Task<List<NetAdapterInfo>> ListAsync() => Task.Run(() =>
    {
        var list = new List<NetAdapterInfo>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex) { throw new InvalidOperationException("Could not enumerate adapters: " + ex.Message, ex); }
        foreach (var nic in nics)
        {
            try { list.Add(ReadOne(nic)); }
            catch (Exception ex)
            {
                list.Add(new NetAdapterInfo
                {
                    Guid = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description + $" (unreadable: {ex.Message})",
                    Kind = NetParsing.ClassifyAdapter(nic.Description, nic.Name),
                    Status = "Unknown",
                });
            }
        }
        return list.OrderBy(a => a.Kind).ThenBy(a => a.Name).ToList();
    });

    private static NetAdapterInfo ReadOne(NetworkInterface nic)
    {
        string guid = nic.Id ?? "";
        string regKey = FindRegistryKey(guid);
        var v4 = new List<string>();
        var v6 = new List<string>();
        var gw = new List<string>();
        var dns = new List<string>();
        bool dhcp = false;
        string dhcpServer = "";
        string mtu = "";
        try
        {
            var props = nic.GetIPProperties();
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4.Add(u.Address + "/" + u.IPv4Mask);
                else if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) v6.Add(u.Address.ToString());
            }
            foreach (var g in props.GatewayAddresses) gw.Add(g.Address.ToString());
            foreach (var d in props.DnsAddresses) dns.Add(d.ToString());
            try
            {
                var ipv4props = props.GetIPv4Properties();
                if (ipv4props != null)
                {
                    mtu = ipv4props.Mtu.ToString();
                    dhcp = ipv4props.IsDhcpEnabled;
                }
                try
                {
                    using var tk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}", false);
                    dhcpServer = tk?.GetValue("DhcpServer")?.ToString() ?? "";
                }
                catch { }
            }
            catch { }
        }
        catch { }
        long speed = 0;
        try { speed = nic.Speed; } catch { }
        string mac = "";
        try
        {
            var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
            if (bytes != null && bytes.Length == 6) mac = BitConverter.ToString(bytes);
        }
        catch { }
        int ifIndex = 0;
        try
        {
            var idx = nic.GetIPProperties().GetIPv4Properties()?.Index;
            if (idx.HasValue) ifIndex = (int)idx.Value;
        }
        catch { }
        var kind = NetParsing.ClassifyAdapter(nic.Description, nic.Name);
        return new NetAdapterInfo
        {
            Guid = guid,
            Name = nic.Name,
            Description = nic.Description,
            Kind = kind,
            Status = nic.OperationalStatus.ToString(),
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            LinkSpeedBps = speed,
            Mac = mac,
            RegistryKey = regKey,
            IPv4 = v4,
            IPv6 = v6,
            Gateways = gw,
            Dns = dns,
            DhcpEnabled = dhcp,
            DhcpServer = dhcpServer,
            Mtu = mtu,
            InterfaceIndex = ifIndex,
        };
    }

    /// <summary>00NN subkey whose NetCfgInstanceId matches the interface GUID (braces tolerant).</summary>
    public static string FindRegistryKey(string guid)
    {
        try
        {
            var norm = (guid ?? "").Trim().Trim('{', '}').ToLowerInvariant();
            if (norm.Length == 0) return "";
            using var baseKey = Registry.LocalMachine.OpenSubKey(ClassBase, false);
            if (baseKey is null) return "";
            foreach (var sub in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var k = baseKey.OpenSubKey(sub, false);
                    var id = (k?.GetValue("NetCfgInstanceId") as string ?? "").Trim().Trim('{', '}').ToLowerInvariant();
                    if (id == norm) return sub;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    public static Task<Dictionary<string, string>> ReadDeviceValuesAsync(string regKey) => Task.Run(() =>
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(regKey)) return dict;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ClassBase + "\\" + regKey, false);
            if (k is null) return dict;
            foreach (var name in k.GetValueNames())
            {
                try { dict[name] = k.GetValue(name)?.ToString() ?? ""; } catch { }
            }
        }
        catch { }
        return dict;
    });

    public static Task<List<NetAdvancedProp>> GetAdvancedAsync(string regKey) => Task.Run(() =>
    {
        var props = new List<NetAdvancedProp>();
        if (string.IsNullOrEmpty(regKey)) return props;
        Dictionary<string, string> current;
        try
        {
            using var dev = Registry.LocalMachine.OpenSubKey(ClassBase + "\\" + regKey, false);
            current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (dev != null)
                foreach (var name in dev.GetValueNames())
                    try { current[name] = dev.GetValue(name)?.ToString() ?? ""; } catch { }
        }
        catch { current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }

        List<string> keywords;
        try
        {
            using var p = Registry.LocalMachine.OpenSubKey(ClassBase + "\\" + regKey + "\\Ndi\\Params", false);
            if (p is null) return props;
            keywords = p.GetSubKeyNames().ToList();
        }
        catch { return props; }

        foreach (var kw in keywords.OrderBy(k => k))
        {
            try { props.Add(ReadOneProp(regKey, kw, current)); }
            catch { /* one bad property never blocks the other 30 */ }
        }
        return props;
    });

    private static NetAdvancedProp ReadOneProp(string regKey, string keyword, Dictionary<string, string> current)
    {
        string path = $"{ClassBase}\\{regKey}\\Ndi\\Params\\{keyword}";
        string display = keyword, type = "edit", def = "";
        long? min = null, max = null, step = null;
        var options = new List<NetAdvancedOption>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path, false);
            if (k != null)
            {
                display = k.GetValue("ParamDesc") as string ?? keyword;
                if (string.IsNullOrWhiteSpace(display))
                    display = keyword + " (driver gave no display name)";
                type = (k.GetValue("type") as string ?? "edit").Trim().ToLowerInvariant();
                def = k.GetValue("default")?.ToString() ?? "";
                if (long.TryParse(k.GetValue("min")?.ToString(), out var mn)) min = mn;
                if (long.TryParse(k.GetValue("max")?.ToString(), out var mx)) max = mx;
                if (long.TryParse(k.GetValue("step")?.ToString(), out var st)) step = st;
                if (type == "enum")
                {
                    try
                    {
                        using var e = k.OpenSubKey("enum");
                        if (e != null)
                            foreach (var v in e.GetValueNames())
                                options.Add(new NetAdvancedOption(v, e.GetValue(v)?.ToString() ?? v));
                    }
                    catch { }
                    options.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.Ordinal));
                }
            }
        }
        catch { }
        current.TryGetValue(keyword, out var cur);
        cur ??= def;
        return new NetAdvancedProp
        {
            Keyword = keyword,
            DisplayName = display,
            Type = type,
            Current = cur ?? "",
            Default = def,
            Options = options,
            Min = min,
            Max = max,
            Step = step,
            Category = NetParsing.CategorizeKeyword(keyword),
            Changed = NetParsing.IsChanged(cur, def),
            Explanation = NetParsing.Explain(keyword, display),
        };
    }

    public static Task<Dictionary<string, string>> GetDhcpLeaseAsync(string guid) => Task.Run(() =>
    {
        var dict = new Dictionary<string, string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}", false);
            if (k is null) return dict;
            foreach (var name in new[] { "LeaseObtainedTime", "LeaseTerminatesTime", "DhcpServer", "DhcpIPAddress", "DhcpSubnetMask", "DhcpDefaultGateway" })
            {
                try
                {
                    var v = k.GetValue(name);
                    if (v is null) continue;
                    if ((name.StartsWith("Lease")) && v is int seconds)
                        dict[name] = NetParsing.LeaseToLocal(seconds).ToString("g");
                    else dict[name] = v.ToString() ?? "";
                }
                catch { }
            }
        }
        catch { }
        return dict;
    });

    public static async Task<string> GetSignerAsync(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return "Not reported by this driver.";
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sys = Path.Combine(sysRoot, "drivers", serviceName.Trim() + ".sys");
        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(sys)) return "Driver file not found at " + sys + ".";
#pragma warning disable SYSLIB0057 // No replacement reads Authenticode signers; this is exactly its job.
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(sys);
#pragma warning restore SYSLIB0057
                return "Signed: " + cert.Subject;
            }
            catch (Exception ex) { return "No readable embedded signature (" + ex.GetBaseException().Message + ")."; }
        }).ConfigureAwait(false);
    }

    public static async Task<List<NetTcpEntry>> GetTcpGlobalAsync()
    {
        var knownDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Receive Window Auto-Tuning Level"] = "normal",
            ["Add-On Congestion Control Provider"] = "default",
            ["ECN Capability"] = "disabled",
            ["RFC 1323 Timestamps"] = "allowed",
            ["Initial RTO"] = "1000",
            ["Receive Segment Coalescing State"] = "enabled",
            ["Non Sack Rtt Resiliency"] = "disabled",
            ["Max SYN Retransmissions"] = "2",
            ["Fast Open"] = "enabled",
            ["Fast Open Fallback"] = "enabled",
            ["HyStart"] = "enabled",
            ["Proportional Rate Reduction"] = "enabled",
            ["Pacing Profile"] = "off",
            ["Receive-Side Scaling State"] = "enabled",
        };
        var r = await NetProcess.RunAsync("netsh", "interface tcp show global", 15000).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException("netsh interface tcp show global failed: " + (r.Stderr.Trim().Length > 0 ? r.Stderr.Trim() : "exit " + r.ExitCode));
        var dict = NetParsing.ParseNetshKeyValues(r.Stdout);
        if (dict.Count == 0) throw new InvalidOperationException("netsh returned no parseable TCP values.");
        return dict.Select(kv => new NetTcpEntry(kv.Key, kv.Value,
            knownDefaults.TryGetValue(kv.Key, out var d)
                ? (string.Equals(d, kv.Value, StringComparison.OrdinalIgnoreCase) ? "Default" : "Changed")
                : "Unknown baseline")).ToList();
    }

    public static async Task<List<NetTcpEntry>> GetUdpGlobalAsync()
    {
        var r = await NetProcess.RunAsync("netsh", "interface udp show global", 15000).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException("netsh interface udp show global failed: " + (r.Stderr.Trim().Length > 0 ? r.Stderr.Trim() : "exit " + r.ExitCode));
        var dict = NetParsing.ParseNetshKeyValues(r.Stdout);
        if (dict.Count == 0) throw new InvalidOperationException("netsh returned no parseable UDP values.");
        // Stock client default for both is enabled.
        return dict.Select(kv => new NetTcpEntry(kv.Key, kv.Value,
            string.Equals("enabled", kv.Value, StringComparison.OrdinalIgnoreCase) ? "Default" : "Changed")).ToList();
    }

    public static async Task<List<(string Iface, List<string> Caps)>> GetOffloadAsync()
    {
        var r = await NetProcess.RunAsync("netsh", "interface ipv4 show offload", 15000).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            throw new InvalidOperationException("netsh interface ipv4 show offload failed: " + (r.Stderr.Trim().Length > 0 ? r.Stderr.Trim() : "exit " + r.ExitCode));
        return NetParsing.ParseNetshOffload(r.Stdout);
    }

    public static async Task<NetWifiInfo> GetWifiAsync(string adapterName)
    {
        var r = await NetProcess.RunAsync("netsh", "wlan show interfaces", 15000).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0)
            return new NetWifiInfo(false, "netsh wlan failed: " + r.Stderr.Trim(), "", "", "", "", "", "", "", "", "", "");
        var blocks = r.Stdout.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var b in blocks)
        {
            var d = NetParsing.ParseNetshKeyValues(b);
            if (!d.TryGetValue("Name", out var name) || !name.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
                continue;
            d.TryGetValue("State", out var state);
            if (!string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase))
                return new NetWifiInfo(false, $"Interface state is '{state ?? "unknown"}' — connect to a network to see live Wi-Fi detail.",
                    "", "", "", "", "", "", "", "", "", "");
            string Get(string k) => d.TryGetValue(k, out var v) ? v : "";
            return new NetWifiInfo(true, "", Get("SSID"), Get("AP BSSID"), Get("Band"),
                Get("Channel"), Get("Radio type"), Get("Signal"), Get("Rssi"),
                Get("Receive rate (Mbps)"), Get("Transmit rate (Mbps)"),
                (Get("Authentication") + " / " + Get("Cipher")).Trim(' ', '/'));
        }
        return new NetWifiInfo(false, "No wireless interface is connected. SSID/BSSID need an active connection" +
            (OperatingSystem.IsWindowsVersionAtLeast(11) ? " (newer Windows 11 builds may also need location permission)." : "."),
            "", "", "", "", "", "", "", "", "", "");
    }

    public static Task<NetStatSample?> GetStatsAsync(string guid) => Task.Run<NetStatSample?>(() =>
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, guid, StringComparison.OrdinalIgnoreCase));
            if (nic is null) return null;
            var s = nic.GetIPv4Statistics();
            return new NetStatSample(DateTime.UtcNow, s.BytesReceived, s.BytesSent);
        }
        catch { return null; }
    });

    public static Task<(long InErr, long OutErr, long InDisc, long OutDisc, long InUcast, long OutUcast, long InNUcast, long OutNUcast)>
        GetCountersAsync(string guid) => Task.Run(() =>
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, guid, StringComparison.OrdinalIgnoreCase));
            if (nic is null) return (0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L);
            var s = nic.GetIPv4Statistics();
            return ((long)s.IncomingPacketsWithErrors, (long)s.OutgoingPacketsWithErrors,
                (long)s.IncomingPacketsDiscarded, (long)s.OutgoingPacketsDiscarded,
                (long)s.UnicastPacketsReceived, (long)s.UnicastPacketsSent,
                (long)s.NonUnicastPacketsReceived, (long)s.NonUnicastPacketsSent);
        }
        catch { return (0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L); }
    });

    public static string SnapshotDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "network", "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static async Task<string> WriteSnapshotAsync(NetSnapshot snap)
    {
        var safe = string.Concat((snap.AdapterName ?? "adapter").Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(SnapshotDir, $"{safe}_{snap.TakenUtc:yyyyMMdd_HHmmss}.json");
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        return path;
    }

    public static async Task<NetSnapshot?> ReadSnapshotAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NetSnapshot>(json);
        }
        catch { return null; }
    }
}
