using Microsoft.Win32;
using kaliteConfig.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace kaliteConfig.Services;

/// <summary>
/// Startup manager data layer: HKCU/HKLM Run values, user-mode services, and
/// non-Microsoft scheduled tasks. Reads are best-effort (a failing source
/// yields an empty section, never an exception); every mutating call returns
/// null on success or a human-readable error. The app manifest already
/// requires admin, which all mutations need.
///
/// Disabling a Run value moves it to a backup store (restorable); deleting
/// removes value and backup permanently. Services toggle between Automatic
/// and Disabled; tasks toggle Enabled.
/// </summary>
public sealed class StartupManagerService
{
    public const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultBackupRoot = @"SOFTWARE\kaliteConfig\StartupBackup";

    private readonly RegistryHive _backupHive;
    private readonly string _backupRoot;

    /// <param name="backupHive">HKLM in production; inject HKCU in tests so the
    /// harness never touches machine state.</param>
    public StartupManagerService(RegistryHive backupHive = RegistryHive.LocalMachine, string? backupRoot = null)
    {
        _backupHive = backupHive;
        _backupRoot = backupRoot ?? DefaultBackupRoot;
    }

    internal sealed record BackupEntry(string RawCommand, string Kind);

    public sealed record StartupScan(
        List<StartupEntry> UserRun,
        List<StartupEntry> MachineRun,
        List<StartupEntry> Services,
        List<StartupEntry> Tasks);

    public async Task<StartupScan> ScanAsync(CancellationToken ct, IProgress<string>? progress = null)
    {
        var user = Task.Run(() => ScanRunValues(RegistryHive.CurrentUser, StartupEntryKind.RunUser), ct);
        var machine = Task.Run(() => ScanRunValues(RegistryHive.LocalMachine, StartupEntryKind.RunMachine), ct);
        var services = Task.Run(() => ScanServices(), ct);
        progress?.Report("Reading startup entries…");
        await Task.WhenAll(user, machine, services).ConfigureAwait(false);
        progress?.Report("Reading scheduled tasks…");
        var tasks = await ScanTasksAsync(ct).ConfigureAwait(false);
        return new StartupScan(await user, await machine, await services, tasks);
    }

    // ---------------- Run values ----------------

    private List<StartupEntry> ScanRunValues(RegistryHive hive, StartupEntryKind kind)
    {
        var present = new Dictionary<string, (string Raw, string Kind)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RunSubKey, false);
            if (key != null)
            {
                foreach (string name in key.GetValueNames())
                {
                    try
                    {
                        var valueKind = key.GetValueKind(name);
                        if (valueKind != RegistryValueKind.String && valueKind != RegistryValueKind.ExpandString)
                            continue;
                        if (key.GetValue(name) is string s)
                            present[name] = (s, valueKind == RegistryValueKind.ExpandString ? "EXPAND_SZ" : "SZ");
                    }
                    catch { /* one bad value never breaks the section */ }
                }
            }
        }
        catch { }
        return MergeRunEntries(kind, present, LoadBackups(AreaFor(kind)));
    }

    private static string AreaFor(StartupEntryKind kind) => kind == StartupEntryKind.RunUser ? "RunUser" : "RunMachine";

    private static string ExpandDisplay(string raw)
    {
        try { return Environment.ExpandEnvironmentVariables(raw ?? ""); }
        catch { return raw ?? ""; }
    }

    /// <summary>
    /// Merges live Run values (checked) with backed-up ones missing from the
    /// registry (unchecked). Pure logic — unit-tested.
    /// </summary>
    internal static List<StartupEntry> MergeRunEntries(
        StartupEntryKind kind,
        IReadOnlyDictionary<string, (string Raw, string Kind)> present,
        IReadOnlyDictionary<string, BackupEntry> backups)
    {
        var list = new List<StartupEntry>();
        foreach (var kv in present)
            list.Add(new StartupEntry
            {
                Kind = kind, Id = kv.Key, Name = kv.Key,
                EntryType = "", Command = ExpandDisplay(kv.Value.Raw), IsEnabled = true,
            });
        foreach (var b in backups)
            if (!present.ContainsKey(b.Key))
                list.Add(new StartupEntry
                {
                    Kind = kind, Id = b.Key, Name = b.Key,
                    EntryType = "", Command = ExpandDisplay(b.Value.RawCommand), IsEnabled = false,
                });
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal Dictionary<string, BackupEntry> LoadBackups(string area)
    {
        var map = new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_backupHive, RegistryView.Registry64);
            using var areaKey = baseKey.OpenSubKey($"{_backupRoot}\\{area}", false);
            if (areaKey == null) return map;
            foreach (string name in areaKey.GetSubKeyNames())
            {
                try
                {
                    using var entry = areaKey.OpenSubKey(name, false);
                    if (entry?.GetValue("Command") is string cmd)
                        map[name] = new BackupEntry(cmd, entry.GetValue("Kind") as string ?? "SZ");
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    internal void SaveBackup(string area, string name, string rawCommand, string kind)
    {
        using var baseKey = RegistryKey.OpenBaseKey(_backupHive, RegistryView.Registry64);
        using var entry = baseKey.CreateSubKey($"{_backupRoot}\\{area}\\{name}", true);
        if (entry == null) throw new InvalidOperationException("Could not open the backup store.");
        entry.SetValue("Command", rawCommand);
        entry.SetValue("Kind", kind);
    }

    internal void RemoveBackup(string area, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_backupHive, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree($"{_backupRoot}\\{area}\\{name}", throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static RegistryValueKind ParseKind(string kind) =>
        kind == "EXPAND_SZ" ? RegistryValueKind.ExpandString : RegistryValueKind.String;

    // ---------------- enable / disable / delete ----------------

    /// <summary>Flips one entry. Returns null on success, else an error message. Never throws.</summary>
    public async Task<string?> SetEnabledAsync(StartupEntry entry, bool enabled)
    {
        try
        {
            return entry.Kind switch
            {
                StartupEntryKind.RunUser => SetRunEnabled(RegistryHive.CurrentUser, "RunUser", entry, enabled),
                StartupEntryKind.RunMachine => SetRunEnabled(RegistryHive.LocalMachine, "RunMachine", entry, enabled),
                StartupEntryKind.Service => SetServiceEnabled(entry.Id, enabled),
                StartupEntryKind.ScheduledTask => await SetTaskEnabledAsync(entry.Id, enabled).ConfigureAwait(false),
                _ => $"Unknown entry kind {entry.Kind}.",
            };
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private string? SetRunEnabled(RegistryHive hive, string area, StartupEntry entry, bool enabled)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            if (enabled)
            {
                var backups = LoadBackups(area);
                if (!backups.TryGetValue(entry.Id, out BackupEntry? backup))
                    return "No backup for this entry — it may have been removed outside the app.";
                using var key = baseKey.CreateSubKey(RunSubKey, true);
                if (key == null) return "Could not open the Run key.";
                key.SetValue(entry.Id, backup.RawCommand, ParseKind(backup.Kind));
                RemoveBackup(area, entry.Id);
                return null;
            }
            using (var key = baseKey.OpenSubKey(RunSubKey, false))
            {
                if (key?.GetValue(entry.Id) is not string raw) return "Entry is no longer present.";
                var kind = "SZ";
                try { if (key.GetValueKind(entry.Id) == RegistryValueKind.ExpandString) kind = "EXPAND_SZ"; } catch { }
                SaveBackup(area, entry.Id, raw, kind);
            }
            using (var key = baseKey.OpenSubKey(RunSubKey, true))
                key?.DeleteValue(entry.Id, throwOnMissingValue: false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Permanently removes one entry (Run deletes drop their backup too). Never throws.</summary>
    public async Task<string?> DeleteAsync(StartupEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case StartupEntryKind.RunUser:
                case StartupEntryKind.RunMachine:
                {
                    var hive = entry.Kind == StartupEntryKind.RunUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(RunSubKey, true);
                    key?.DeleteValue(entry.Id, throwOnMissingValue: false);
                    RemoveBackup(AreaFor(entry.Kind), entry.Id);
                    return null;
                }
                case StartupEntryKind.Service:
                    return DeleteService(entry.Id);
                case StartupEntryKind.ScheduledTask:
                    return await DeleteTaskAsync(entry.Id).ConfigureAwait(false);
                default:
                    return $"Unknown entry kind {entry.Kind}.";
            }
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------------- services (WMI) ----------------

    private static List<StartupEntry> ScanServices()
    {
        var list = new List<StartupEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName, StartMode, ServiceType FROM Win32_Service");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    string type = (mo["ServiceType"] as string) ?? "";
                    if (!type.Equals("Own Process", StringComparison.OrdinalIgnoreCase)
                        && !type.Equals("Share Process", StringComparison.OrdinalIgnoreCase)
                        && !type.Equals("Interactive Process", StringComparison.OrdinalIgnoreCase))
                        continue; // kernel/file-system drivers are not startup apps
                    string name = (mo["Name"] as string) ?? "";
                    if (name.Length == 0) continue;
                    string display = (mo["DisplayName"] as string) ?? "";
                    if (display.Length == 0) display = name;
                    string mode = (mo["StartMode"] as string) ?? "Unknown";
                    string path = (mo["PathName"] as string) ?? "";
                    list.Add(new StartupEntry
                    {
                        Kind = StartupEntryKind.Service,
                        Id = name,
                        Name = display,
                        EntryType = mode,
                        Command = path,
                        IsEnabled = ServiceChecked(mode),
                        Company = CompanyOf(path),
                    });
                }
                catch { /* one bad service never breaks the section */ }
            }
        }
        catch { }
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Checked means auto-starting. Manual and Disabled both show unchecked.</summary>
    internal static bool ServiceChecked(string? startMode) =>
        "Auto".Equals(startMode?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string CompanyOf(string? pathName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pathName)) return "";
            string p = pathName.Trim();
            if (p.StartsWith('"'))
            {
                int end = p.IndexOf('"', 1);
                if (end > 1) p = p.Substring(1, end - 1);
            }
            else
            {
                int space = p.IndexOf(' ');
                if (space > 0) p = p.Substring(0, space);
            }
            p = Environment.ExpandEnvironmentVariables(p);
            if (!File.Exists(p)) return "";
            return FileVersionInfo.GetVersionInfo(p).CompanyName ?? "";
        }
        catch { return ""; }
    }

    internal static bool IsMicrosoftCompany(string? company) =>
        (company ?? "").IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? SetServiceEnabled(string serviceName, bool enabled)
    {
        try
        {
            using var mo = new ManagementObject($"Win32_Service.Name='{serviceName.Replace("'", "''")}'");
            mo.Get();
            var result = mo.InvokeMethod("ChangeStartMode", new object[] { enabled ? "Automatic" : "Disabled" });
            uint rc = result is ManagementBaseObject mbo && mbo["ReturnValue"] is uint u ? u : 999;
            return rc == 0 ? null : $"Could not change start mode (WMI returned {rc}).";
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? DeleteService(string serviceName)
    {
        try
        {
            using var mo = new ManagementObject($"Win32_Service.Name='{serviceName.Replace("'", "''")}'");
            mo.Get();
            var result = mo.InvokeMethod("Delete", null);
            uint rc = result is ManagementBaseObject mbo && mbo["ReturnValue"] is uint u ? u : 999;
            return rc == 0 ? null : $"Could not delete the service (WMI returned {rc}).";
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------------- scheduled tasks (schtasks.exe) ----------------

    private async Task<List<StartupEntry>> ScanTasksAsync(CancellationToken ct)
    {
        var list = new List<StartupEntry>();
        string csv;
        try { csv = await RunSchtasksAsync("/query /fo csv /v", 30_000, ct).ConfigureAwait(false); }
        catch { return list; }
        var rows = ParseTaskListCsv(csv);
        using var gate = new SemaphoreSlim(4);
        var bag = new ConcurrentBag<StartupEntry>();
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;
                string xml = await RunSchtasksAsync($"/query /tn \"{row.Name}\" /xml", 20_000, ct).ConfigureAwait(false);
                bag.Add(new StartupEntry
                {
                    Kind = StartupEntryKind.ScheduledTask,
                    Id = row.Name,
                    Name = row.Name.TrimStart('\\'),
                    EntryType = ParseTaskTriggers(xml),
                    Command = ParseTaskExec(xml),
                    IsEnabled = TaskEnabled(row.State),
                });
            }
            catch { /* one bad task drops just that row */ }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return bag.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string?> SetTaskEnabledAsync(string taskName, bool enabled)
    {
        try
        {
            _ = await RunSchtasksAsync($"/change /tn \"{taskName}\" /{(enabled ? "enable" : "disable")}", 30_000, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static async Task<string?> DeleteTaskAsync(string taskName)
    {
        try
        {
            _ = await RunSchtasksAsync($"/delete /tn \"{taskName}\" /f", 30_000, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Administrator rights are required. Restart kaliteConfig as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    internal static async Task<string> RunSchtasksAsync(string arguments, int timeoutMs, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!proc.Start()) throw new InvalidOperationException("Could not start schtasks.exe.");
        // Drain BOTH pipes from the start (same wedge class as pnputil: a child
        // blocked on a full output buffer never exits). Awaiting all the way
        // also keeps throws on a visible async chain instead of surfacing as
        // debugger "user-unhandled" noise from inside a Task.Run delegate.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("schtasks timed out.");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"schtasks exited with code {proc.ExitCode}." : stderr.Trim());
        return stdout;
    }

    /// <summary>
    /// Parses verbose schtasks CSV into (TaskName, Scheduled Task State,
    /// Task To Run). Skips the \Microsoft\ subtree (Windows' own tasks are
    /// not startup clutter). Header-matched with positional fallback so
    /// localized Windows builds still parse. Pure logic — unit-tested.
    /// </summary>
    internal static List<(string Name, string State, string Command)> ParseTaskListCsv(string csv)
    {
        var rows = new List<(string Name, string State, string Command)>();
        string[] lines = (csv ?? "").Split('\n');
        int nameIdx = 1, stateIdx = 11, cmdIdx = 8;
        bool headerSeen = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            List<string> fields = SplitCsv(line);
            if (fields.Count == 0) continue;
            if (!headerSeen)
            {
                headerSeen = true;
                int ni = IndexOfField(fields, "TaskName");
                int si = IndexOfField(fields, "Scheduled Task State");
                int ci = IndexOfField(fields, "Task To Run");
                if (ni >= 0) nameIdx = ni;
                if (si >= 0) stateIdx = si;
                if (ci >= 0) cmdIdx = ci;
                continue;
            }
            if (nameIdx >= fields.Count) continue;
            // A row shorter than the columns we need is corrupt output, not
            // a task with unknown state — skipping beats mislabeling it.
            int need = Math.Max(nameIdx, Math.Max(stateIdx, cmdIdx));
            if (fields.Count <= need) continue;
            string name = fields[nameIdx].Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                || name.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add((
                name,
                stateIdx < fields.Count ? fields[stateIdx].Trim() : "",
                cmdIdx < fields.Count ? fields[cmdIdx].Trim() : ""));
        }
        return rows;
    }

    private static int IndexOfField(List<string> fields, string name)
    {
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Trim().Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Minimal quoted-CSV splitter (handles "" escapes). Pure — unit-tested.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>
    /// Trigger kind from a task's XML (namespace-aware): Logon, Boot, Time,
    /// Scheduled, Event, Session, Multiple, or — when triggerless. Pure — unit-tested.
    /// </summary>
    internal static string ParseTaskTriggers(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var triggers = doc.Root?.Element(ns + "Triggers");
            if (triggers == null) return "—";
            var kinds = triggers.Elements()
                .Select(e => e.Name.LocalName switch
                {
                    "LogonTrigger" => "Logon",
                    "BootTrigger" => "Boot",
                    "TimeTrigger" => "Time",
                    "CalendarTrigger" => "Scheduled",
                    "EventTrigger" => "Event",
                    "SessionStateChangeTrigger" => "Session",
                    "RegistrationTrigger" => "Manual",
                    _ => e.Name.LocalName.Replace("Trigger", ""),
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (kinds.Count == 0) return "—";
            return kinds.Count == 1 ? kinds[0] : "Multiple";
        }
        catch { return "—"; }
    }

    /// <summary>First Exec action's command line ("cmd args", plus a count suffix). Pure — unit-tested.</summary>
    internal static string ParseTaskExec(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var execs = doc.Root?.Element(ns + "Actions")?.Elements(ns + "Exec").ToList();
            if (execs == null || execs.Count == 0) return "—";
            string cmd = (execs[0].Element(ns + "Command")?.Value ?? "").Trim();
            string args = (execs[0].Element(ns + "Arguments")?.Value ?? "").Trim();
            string line = (cmd + " " + args).Trim();
            if (line.Length == 0) line = "—";
            return execs.Count > 1 ? $"{line} (+{execs.Count - 1} more)" : line;
        }
        catch { return "—"; }
    }

    /// <summary>Checked unless explicitly Disabled (Ready/Running/Queued count as on).</summary>
    internal static bool TaskEnabled(string? state) =>
        !string.IsNullOrWhiteSpace(state) && !state.Trim().Equals("Disabled", StringComparison.OrdinalIgnoreCase);
}
