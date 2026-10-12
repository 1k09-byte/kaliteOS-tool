// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

/// <summary>System-wide network settings with proven setters. TCP/UDP write through
/// netsh (in-process, instant, no restart); offload and RSS globals write through an
/// awaited powershell.exe (no netsh equivalent exists) with timeout, kill switch and
/// mandatory read-back. Every row below was verified settable on this machine.</summary>
public static class NetSysService
{
    public sealed record RowState(NetParsing.SysRowDef Def, string Current);

    public static async Task<List<RowState>> GetRowsAsync(string adapterName)
    {
        var defs = NetParsing.SysRowDefs();
        var tcp = await ReadTcpAsync().ConfigureAwait(false);
        var udp = await ReadUdpAsync().ConfigureAwait(false);
        var off = await ReadOffloadAsync().ConfigureAwait(false);
        var rss = await ReadRssAsync(adapterName).ConfigureAwait(false);
        var out_ = new List<RowState>();
        foreach (var d in defs)
        {
            string cur = d.Setter switch
            {
                "NetshTcp" => tcp.TryGetValue(d.Key, out var t) ? t : "",
                "NetshSupplemental" => tcp.TryGetValue(d.Key, out var s) ? s : "",
                "NetshUdp" => udp.TryGetValue(d.Key, out var u) ? u : "",
                "PsOffload" => off.TryGetValue(d.Key, out var o) ? o : "",
                "PsRss" => rss.TryGetValue(d.Key, out var r) ? r : "",
                _ => "",
            };
            out_.Add(new RowState(d, cur));
        }
        return out_;
    }

    private static async Task<Dictionary<string, string>> ReadTcpAsync()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = await NetProcess.RunAsync("netsh", "interface tcp show global", 15000).ConfigureAwait(false);
            if (!r.TimedOut && r.ExitCode == 0)
                foreach (var kv in NetParsing.ParseNetshKeyValues(r.Stdout)) dict[kv.Key] = kv.Value;
        }
        catch { }
        try
        {
            var r = await NetProcess.RunAsync("netsh", "interface tcp show supplemental", 15000).ConfigureAwait(false);
            if (!r.TimedOut && r.ExitCode == 0)
                foreach (var kv in NetParsing.ParseNetshKeyValues(r.Stdout)) dict[kv.Key] = kv.Value;
        }
        catch { }
        return dict;
    }

    private static async Task<Dictionary<string, string>> ReadUdpAsync()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = await NetProcess.RunAsync("netsh", "interface udp show global", 15000).ConfigureAwait(false);
            if (!r.TimedOut && r.ExitCode == 0)
                foreach (var kv in NetParsing.ParseNetshKeyValues(r.Stdout)) dict[kv.Key] = kv.Value;
        }
        catch { }
        return dict;
    }

    private static async Task<string> PsJsonAsync(string script)
    {
        var r = await NetProcess.RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" + script + "\"", 25000).ConfigureAwait(false);
        if (r.TimedOut) throw new InvalidOperationException("PowerShell query timed out and was killed.");
        if (r.ExitCode != 0) throw new InvalidOperationException("PowerShell query failed: " + r.Stderr.Trim());
        return r.Stdout;
    }

    private static async Task<Dictionary<string, string>> ReadOffloadAsync()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await PsJsonAsync(
                "Get-NetOffloadGlobalSetting | Select-Object ReceiveSideScaling, ReceiveSegmentCoalescing, " +
                "TaskOffload, NetworkDirect, NetworkDirectAcrossIPSubnets, PacketCoalescingFilter, Chimney | ConvertTo-Json -Compress").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json.Trim());
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                int v = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : 0;
                dict[prop.Name] = NetParsing.OffloadDisplay(prop.Name, v);
            }
        }
        catch (Exception ex)
        {
            dict["__error"] = ex.Message;
        }
        return dict;
    }

    private static async Task<Dictionary<string, string>> ReadRssAsync(string adapterName)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(adapterName)) return dict;
        try
        {
            var json = await PsJsonAsync(
                $"Get-NetAdapterRss -Name '{adapterName.Replace("'", "''")}' | " +
                "Select-Object Profile, BaseProcessorNumber, MaxProcessorNumber, MaxProcessors, NumberOfReceiveQueues | ConvertTo-Json -Compress").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            if (root.TryGetProperty("Profile", out var p)) dict["RSS Profile"] = NetParsing.RssProfileName(p.GetInt32());
            if (root.TryGetProperty("BaseProcessorNumber", out var b)) dict["RSS Base Processor"] = b.GetInt32().ToString();
            if (root.TryGetProperty("MaxProcessors", out var m)) dict["RSS Max Processor"] = m.GetInt32().ToString();
            if (root.TryGetProperty("NumberOfReceiveQueues", out var q)) dict["RSS Queues"] = q.GetInt32().ToString();
        }
        catch (Exception ex)
        {
            dict["__error"] = ex.Message;
        }
        return dict;
    }

    /// <summary>Writes one row and reads it back. Returns (applied, readBack, message).</summary>
    public static async Task<(bool Ok, string ReadBack, string Message)> ApplyRowAsync(
        NetParsing.SysRowDef def, string adapterName, string value)
    {
        if (!NetEditService.IsElevated())
            return (false, "", NetEditService.ElevationMessage("system network settings"));
        var err = NetParsing.ValidateSysValue(def, value);
        if (err != null) return (false, "", err);
        try
        {
            var (tool, args) = NetParsing.SysSetterCommand(def, adapterName, value);
            var r = await NetProcess.RunAsync(tool, args, 25000).ConfigureAwait(false);
            if (r.TimedOut || r.ExitCode != 0)
                return (false, "", $"Write failed (exit {r.ExitCode}): " + r.Stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, "", "Write failed: " + ex.GetBaseException().Message);
        }
        await Task.Delay(500).ConfigureAwait(false);
        string back = await ReadOneAsync(def, adapterName).ConfigureAwait(false);
        // Windows answers in its own casing ("enabled" for a write of "Enabled"), so
        // compare case-insensitively and hand the UI the canonical spelling.
        bool match = def.Kind == NetParsing.SysKind.Number
            ? back == value
            : string.Equals(NetParsing.CanonicalOption(def, back), NetParsing.CanonicalOption(def, value),
                StringComparison.OrdinalIgnoreCase);
        string shown = NetParsing.CanonicalOption(def, back);
        return match
            ? (true, shown, $"Applied, read back '{shown}'.")
            : (false, shown, $"Write returned no error but read-back is '{shown}' instead of '{value}'.");
    }

    private static async Task<string> ReadOneAsync(NetParsing.SysRowDef def, string adapterName)
    {
        var rows = await GetRowsAsync(adapterName).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.Def.Section == def.Section && r.Def.Key == def.Key)?.Current ?? "";
    }
}
