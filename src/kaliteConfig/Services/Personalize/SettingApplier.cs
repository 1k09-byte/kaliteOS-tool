using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Win32;

namespace kaliteConfig.Services.Personalize;

public sealed class SettingApplier
{
    private readonly SnapshotStore _snapshots;

    public SettingApplier(SnapshotStore snapshots) => _snapshots = snapshots;

    public IReadOnlyList<SettingResult> Apply(IEnumerable<SettingDef> defs, string? broadcastArea = null)
    {
        var results = new List<SettingResult>();
        foreach (var def in defs) results.Add(ApplyOne(def));
        if (broadcastArea is not null && results.Any(r => r.Ok))
            SystemNotifier.BroadcastSettingChange(broadcastArea);
        return results;
    }

    private SettingResult ApplyOne(SettingDef def)
    {
        bool snapshotIsNew = !_snapshots.Has(def.Id);
        SnapshotEntry? before = null;
        try
        {
            before = SnapshotStore.ReadCurrent(def.Id, def.SubKey, def.Name);
            _snapshots.CaptureIfNeeded(def.Id, def.SubKey, def.Name);

            HkcuRegistry.Write(def.SubKey, def.Name, def.Value, def.Kind);
            bool liveOk = def.LiveSync?.Invoke() ?? true;

            var readBack = HkcuRegistry.ReadEntry(def.SubKey, def.Name);
            if (liveOk && readBack.Exists && HkcuRegistry.SameValue(readBack.Data, def.Value))
                return new SettingResult(def.Id, true, "Applied.");

            string reason = liveOk
                ? "Windows did not keep the new value. It may be locked by policy."
                : "Windows rejected the change.";
            return Rollback(def, before, snapshotIsNew, reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Rollback(def, before, snapshotIsNew, ex.Message);
        }
    }

    private SettingResult Rollback(SettingDef def, SnapshotEntry? before, bool snapshotIsNew, string reason)
    {
        string message = reason;
        try
        {
            if (before is not null) RestoreEntry(before);
            if (snapshotIsNew) _snapshots.Remove(def.Id);
        }
        catch (Exception ex)
        {
            message = $"{reason} Rollback also failed: {ex.Message}";
        }

        try
        {
            if (def.LiveSync is not null && !def.LiveSync())
                message += " Live state could not be re-synced.";
        }
        catch (Exception ex)
        {
            message += $" Live re-sync failed: {ex.Message}";
        }

        return new SettingResult(def.Id, false, message);
    }

    public IReadOnlyList<SettingResult> RevertWhere(
        Func<SnapshotEntry, bool> pick,
        IReadOnlyList<(string Area, Func<bool> Sync)> liveSyncs,
        string? broadcastArea = null)
    {
        var results = new List<SettingResult>();
        foreach (var entry in _snapshots.All().Where(pick))
        {
            try
            {
                RestoreEntry(entry);
                _snapshots.Remove(entry.Id);
                results.Add(new SettingResult(entry.Id, true, "Reverted to the value before this app changed it."));
            }
            catch (Exception ex)
            {
                results.Add(new SettingResult(entry.Id, false, ex.Message));
            }
        }

        foreach (var (area, sync) in liveSyncs)
        {
            try
            {
                if (!sync())
                    results.Add(new SettingResult("live:" + area, false, "Windows did not accept the live update."));
            }
            catch (Exception ex)
            {
                results.Add(new SettingResult("live:" + area, false, ex.Message));
            }
        }

        if (broadcastArea is not null) SystemNotifier.BroadcastSettingChange(broadcastArea);
        return results;
    }

    internal static void RestoreEntry(SnapshotEntry e)
    {
        if (!e.Existed)
        {
            HkcuRegistry.Delete(e.SubKey, e.Name);
            return;
        }
        if (e.Kind == "DWord")
            HkcuRegistry.Write(e.SubKey, e.Name, int.Parse(e.Value ?? "0", CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else if (e.Kind == "Binary")
            HkcuRegistry.Write(e.SubKey, e.Name, SnapshotStore.RestoreValue(e)!, RegistryValueKind.Binary);
        else
            HkcuRegistry.Write(e.SubKey, e.Name, e.Value ?? "", RegistryValueKind.String);
    }
}
