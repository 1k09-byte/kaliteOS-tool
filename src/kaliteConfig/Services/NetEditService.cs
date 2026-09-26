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
using System.Threading.Tasks;
using Microsoft.Win32;

namespace kaliteConfig.Services;

/// <summary>Writes driver settings with a safety net. Reading needs no elevation;
/// every write requires it and says so truthfully when it is missing.
/// Flow: snapshot -&gt; validate -&gt; diff preview -&gt; apply (registry + adapter
/// restart) -&gt; read back -&gt; 20 s keep-countdown with auto-revert from the
/// snapshot (registry writes only, so the revert works with the network down).</summary>
public static class NetEditService
{
    private const string ClassBase = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static bool IsElevated()
    {
        // Same truthfulness rule as WindowsSettingsService: real token check, never throws.
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string ElevationMessage(string what)
    {
        if (!IsElevated())
            return $"Administrator rights are required to change {what}. Restart kaliteConfig as administrator and try again. Controls stay read-only until then.";
        return $"Access was denied writing {what} even though kaliteConfig IS running elevated.";
    }

    public static bool IsRemoteSession() =>
        (Environment.GetEnvironmentVariable("SESSIONNAME") ?? "").StartsWith("RDP-", StringComparison.OrdinalIgnoreCase);

    public static async Task<NetSnapshot> SnapshotAsync(string adapterGuid, string adapterName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = await NetAdapterService.GetAdvancedAsync(
            NetAdapterService.FindRegistryKey(adapterGuid)).ConfigureAwait(false);
        foreach (var p in props) values[p.Keyword] = p.Current;
        var snap = new NetSnapshot(adapterGuid, adapterName, DateTime.UtcNow, values);
        await NetAdapterService.WriteSnapshotAsync(snap).ConfigureAwait(false);
        return snap;
    }

    public sealed record ApplyResult(bool Ok, string Message, List<(string Keyword, bool Applied, string Reason)> PerSetting);

    /// <summary>Writes validated values, restarts the adapter once, reads everything back.</summary>
    public static async Task<ApplyResult> ApplyAsync(
        string adapterGuid, string adapterName, string regKey,
        List<NetAdvancedProp> props, Dictionary<string, string> wanted)
    {
        var per = new List<(string Keyword, bool Applied, string Reason)>();
        if (!IsElevated())
            return new ApplyResult(false, ElevationMessage($"adapter {adapterName} settings"), per);
        if (string.IsNullOrEmpty(regKey))
            return new ApplyResult(false, "No driver registry key is known for this adapter, so nothing was written.", per);

        var byKw = props.ToDictionary(p => p.Keyword, StringComparer.OrdinalIgnoreCase);
        var toWrite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in wanted)
        {
            if (!byKw.TryGetValue(kv.Key, out var prop))
            {
                per.Add((kv.Key, false, "The current driver does not expose this setting; skipped."));
                continue;
            }
            var err = NetParsing.ValidateValue(prop, kv.Value);
            if (err != null) { per.Add((kv.Key, false, err)); continue; }
            toWrite[kv.Key] = kv.Value;
        }
        if (toWrite.Count == 0)
            return new ApplyResult(false, "Nothing valid to apply.", per);

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ClassBase + "\\" + regKey, true);
            if (k is null) return new ApplyResult(false, "Could not open the driver key for writing.", per);
            foreach (var kv in toWrite) k.SetValue(kv.Key, kv.Value, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            return new ApplyResult(false, ElevationMessage($"adapter {adapterName} settings") + " (" + ex.GetBaseException().Message + ")", per);
        }

        var restarted = await RestartAdapterAsync(adapterName).ConfigureAwait(false);
        if (!restarted.Ok)
            return new ApplyResult(false, "Values were written but " + restarted.Message + " Read-back skipped.", per);

        var live = await NetAdapterService.GetAdvancedAsync(regKey).ConfigureAwait(false);
        var liveDict = live.ToDictionary(p => p.Keyword, p => p.Current, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in toWrite)
        {
            liveDict.TryGetValue(kv.Key, out var got);
            per.Add((kv.Key, string.Equals(got ?? "", kv.Value, StringComparison.Ordinal),
                string.Equals(got ?? "", kv.Value, StringComparison.Ordinal)
                    ? $"Read back '{got}'."
                    : $"Read back '{got ?? "(missing)"}' instead of '{kv.Value}'."));
        }
        bool allOk = per.All(p => p.Applied);
        return new ApplyResult(allOk,
            allOk ? $"{toWrite.Count} setting(s) applied and read back." : "Some settings did not read back; see per-setting reasons.",
            per);
    }

    public static async Task<(bool Ok, string Message)> RestartAdapterAsync(string adapterName)
    {
        var off = await NetProcess.RunAsync("netsh", $"interface set interface name=\"{adapterName}\" admin=disabled", 20000).ConfigureAwait(false);
        if (off.TimedOut || off.ExitCode != 0)
            return (false, "could not disable the adapter: " + (off.Stderr.Trim().Length > 0 ? off.Stderr.Trim() : "exit " + off.ExitCode));
        await Task.Delay(1500).ConfigureAwait(false);
        var on = await NetProcess.RunAsync("netsh", $"interface set interface name=\"{adapterName}\" admin=enabled", 20000).ConfigureAwait(false);
        if (on.TimedOut || on.ExitCode != 0)
            return (false, "adapter was disabled but re-enable failed (" + (on.Stderr.Trim().Length > 0 ? on.Stderr.Trim() : "exit " + on.ExitCode) + "). Re-enable it in Network Connections.");
        await Task.Delay(2500).ConfigureAwait(false);
        return (true, "Adapter restarted.");
    }

    public static async Task<ApplyResult> RollbackAsync(NetSnapshot snap, string adapterName, string regKey)
    {
        var live = await NetAdapterService.GetAdvancedAsync(regKey).ConfigureAwait(false);
        var plan = NetParsing.RollbackPlan(snap.Values,
            live.ToDictionary(p => p.Keyword, p => p.Current, StringComparer.OrdinalIgnoreCase),
            k => k);
        if (plan.Count == 0)
            return new ApplyResult(true, "Already matches the snapshot; nothing to revert.", new List<(string Keyword, bool Applied, string Reason)>());
        var wanted = plan.ToDictionary(p => p.Keyword, p => snap.Values[p.Keyword], StringComparer.OrdinalIgnoreCase);
        return await ApplyAsync(snap.AdapterGuid, adapterName, regKey, live, wanted).ConfigureAwait(false);
    }

    public static async Task<ApplyResult> ResetToDefaultsAsync(string adapterGuid, string adapterName, string regKey)
    {
        var live = await NetAdapterService.GetAdvancedAsync(regKey).ConfigureAwait(false);
        var wanted = live.Where(p => NetParsing.IsChanged(p.Current, p.Default))
            .ToDictionary(p => p.Keyword, p => p.Default, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return new ApplyResult(true, "Every setting already matches its driver default.", new List<(string Keyword, bool Applied, string Reason)>());
        return await ApplyAsync(adapterGuid, adapterName, regKey, live, wanted).ConfigureAwait(false);
    }
}

