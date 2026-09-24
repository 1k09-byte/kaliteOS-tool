using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Models;
using kaliteConfig.Services;
using Microsoft.Win32;

namespace StartupVerify
{
    /// <summary>
    /// Offline checks for the startup manager: schtasks CSV/XML parsing,
    /// toggle mappings, Run merge logic, and the backup store round-trip in
    /// an HKCU sandbox (machine state untouched). Live schtasks/WMI/service
    /// mutation is never exercised here.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }

        private static int Main(string[] args)
        {
            Console.WriteLine("=== Startup manager verification (offline) ===");

            // ---- schtasks verbose CSV (quoted commas!) ----
            const string csv =
                "\"HostName\",\"TaskName\",\"Next Run Time\",\"Status\",\"Logon Mode\",\"Last Run Time\",\"Last Result\",\"Author\",\"Task To Run\",\"Start In\",\"Comment\",\"Scheduled Task State\",\"Idle Time\",\"Power Management\"\n" +
                "\"X\",\"\\GoogleUpdateTaskSystem\",\"N/A\",\"Ready\",\"SYSTEM\",\"-1\",\"0\",\"Google\",\"\\\"C:\\Program Files (x86)\\Google\\Update\\updater.exe\\\" --wake --system\",\"N/A\",\"Keeps Google current\",\"Ready\",\"Disabled\",\"Stop on battery\"\n" +
                "\"X\",\"\\Microsoft\\Windows\\Update\\Updater\",\"N/A\",\"Ready\",\"SYSTEM\",\"-1\",\"0\",\"MS\",\"C:\\Windows\\updater.exe\",\"N/A\",\"OS task\",\"Ready\",\"Disabled\",\"Stop\"\n" +
                "\"X\",\"\\BrokenRow\"\n";
            var rows = StartupManagerService.ParseTaskListCsv(csv);
            Check(rows.Count == 1, $"one row parsed, MS subtree + short row skipped (got {rows.Count})");
            if (rows.Count == 1)
            {
                Check(rows[0].Name == "\\GoogleUpdateTaskSystem", $"task name (got '{rows[0].Name}')");
                Check(rows[0].State == "Ready", $"state (got '{rows[0].State}')");
                Check(rows[0].Command.Contains("updater.exe") && rows[0].Command.Contains("--wake"),
                    $"quoted command with comma intact (got '{rows[0].Command}')");
            }

            // ---- trigger XML (real namespace!) ----
            const string logonXml =
                @"<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>" +
                @"<Actions><Exec><Command>C:\a.exe</Command><Arguments>--wake</Arguments></Exec></Actions></Task>";
            Check(StartupManagerService.ParseTaskTriggers(logonXml) == "Logon", "LogonTrigger mapped");
            Check(StartupManagerService.ParseTaskExec(logonXml) == @"C:\a.exe --wake", "exec command+args joined");

            const string multiXml =
                @"<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">" +
                "<Triggers><BootTrigger /><CalendarTrigger /></Triggers>" +
                @"<Actions><Exec><Command>a.exe</Command></Exec><Exec><Command>b.exe</Command></Exec></Actions></Task>";
            Check(StartupManagerService.ParseTaskTriggers(multiXml) == "Multiple", "mixed triggers collapse");
            Check(StartupManagerService.ParseTaskExec(multiXml).Contains("(+1 more)"), "extra actions noted");

            const string bootXml =
                @"<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">" +
                "<Triggers><BootTrigger /></Triggers><Actions /></Task>";
            Check(StartupManagerService.ParseTaskTriggers(bootXml) == "Boot", "BootTrigger mapped");
            Check(StartupManagerService.ParseTaskTriggers("not xml") == "-", "garbage XML yields em-dash");
            Check(StartupManagerService.ParseTaskTriggers(
                @"<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><Triggers /></Task>") == "-",
                "triggerless task yields em-dash");

            // ---- toggle mappings ----
            Check(StartupManagerService.ServiceChecked("Auto"), "service Auto checked");
            Check(!StartupManagerService.ServiceChecked("Manual"), "service Manual unchecked");
            Check(!StartupManagerService.ServiceChecked("Disabled"), "service Disabled unchecked");
            Check(!StartupManagerService.ServiceChecked(null), "service null unchecked");
            Check(StartupManagerService.TaskEnabled("Enabled"), "task Enabled checked");
            Check(StartupManagerService.TaskEnabled("Ready"), "task Ready counts as on");
            Check(StartupManagerService.TaskEnabled("Running"), "task Running counts as on");
            Check(!StartupManagerService.TaskEnabled("Disabled"), "task Disabled unchecked");
            Check(!StartupManagerService.TaskEnabled(""), "task blank unchecked");
            Check(StartupManagerService.IsMicrosoftCompany("Microsoft Corporation"), "MS company detected");
            Check(!StartupManagerService.IsMicrosoftCompany("NVIDIA"), "non-MS company kept");

            // ---- Run merge logic ----
            var present = new Dictionary<string, (string Raw, string Kind)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Discord"] = ("C:\\d.exe --start", "SZ"),
            };
            var backups = new Dictionary<string, StartupManagerService.BackupEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["OldApp"] = new StartupManagerService.BackupEntry("C:\\old.exe", "SZ"),
                ["Discord"] = new StartupManagerService.BackupEntry("STALE", "SZ"), // present wins
            };
            var merged = StartupManagerService.MergeRunEntries(
                StartupEntryKind.RunUser, present, backups);
            Check(merged.Count == 2, $"present + missing-backup merge (got {merged.Count})");
            var discord = merged.Find(e => e.Id == "Discord");
            var old = merged.Find(e => e.Id == "OldApp");
            Check(discord is not null && discord.IsEnabled && discord.Command.Contains("d.exe"),
                "present entry checked with live command");
            Check(old is not null && !old.IsEnabled && old.Command.Contains("old.exe"),
                "missing entry unchecked with backup command");

            // ---- backup store round-trip (HKCU sandbox only) ----
            string sandbox = @"SOFTWARE\StartupVerifyTest\" + Guid.NewGuid().ToString("N");
            var svc = new StartupManagerService(RegistryHive.CurrentUser, sandbox);
            svc.SaveBackup("RunUser", "TestApp", "C:\\test.exe --x", "SZ");
            var loaded = svc.LoadBackups("RunUser");
            Check(loaded.TryGetValue("TestApp", out var be) && be.RawCommand == "C:\\test.exe --x" && be.Kind == "SZ",
                "backup round-trips command and kind");
            svc.RemoveBackup("RunUser", "TestApp");
            Check(!svc.LoadBackups("RunUser").ContainsKey("TestApp"), "backup removal sticks");
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
                Check(true, "sandbox cleaned up");
            }
            catch (Exception ex) { Check(false, "sandbox cleanup: " + ex.Message); }

            Console.WriteLine();
            if (args.Contains("live", StringComparer.OrdinalIgnoreCase))
                LiveScan();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Read-only live scan: exercises the real schtasks/WMI/registry path
        /// (including the async pipe-drained runner) without mutating anything.
        /// Structural invariants only - counts vary by machine.
        /// </summary>
        private static void LiveScan()
        {
            Console.WriteLine("--- live read-only scan ---");
            var svc = new StartupManagerService();
            StartupManagerService.StartupScan? scan = null;
            bool threw = false;
            try
            {
                scan = svc.ScanAsync(
                    System.Threading.CancellationToken.None, null).GetAwaiter().GetResult();
            }
            catch (Exception ex) { threw = true; Console.WriteLine("  threw: " + ex.GetType().Name + ": " + ex.Message); }
            Check(!threw, "live scan completes without throwing");
            if (scan is null) return;
            Check(scan.Tasks.All(t => t.Name.Length > 0), $"all {scan.Tasks.Count} task rows have names");
            Check(scan.Tasks.All(t => !t.Name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)),
                "no Microsoft-subtree tasks listed");
            Check(scan.Tasks.All(t => t.Command.Length > 0), "all task rows have a command line");
            Check(scan.Services.All(s => s.Name.Length > 0), $"all {scan.Services.Count} service rows have names");
            Console.WriteLine($"  (hkcu={scan.UserRun.Count}, hklm={scan.MachineRun.Count}, " +
                              $"services={scan.Services.Count}, tasks={scan.Tasks.Count})");
        }
    }
}
