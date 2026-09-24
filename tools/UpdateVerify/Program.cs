using System;
using System.Linq;
using System.Text.Json;
using kaliteConfig.Services;

namespace UpdateVerify
{
    /// <summary>
    /// Offline checks for the auto-update loop guards: version comparison and
    /// version-matched asset selection. No network, no GPU - runs anywhere.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) _failures++;
        }

        private static int Main()
        {
            Console.WriteLine("=== Updater logic verification (offline) ===");

            // ---- IsNewer ----
            Check(UpdateCheckService.IsNewer("v0.3.0.2", "0.3.0.1"), "newer patch offered");
            Check(!UpdateCheckService.IsNewer("v0.3.0.1", "0.3.0.1"), "identical version never offered (no loop)");
            Check(!UpdateCheckService.IsNewer("v0.3.0.1", "0.3.0.2"), "older release never offered (no downgrade)");
            Check(UpdateCheckService.IsNewer("v0.3.0.10", "0.3.0.9"), "numeric (not lexicographic) compare up");
            Check(!UpdateCheckService.IsNewer("v0.3.0.9", "0.3.0.10"), "numeric (not lexicographic) compare down");
            Check(!UpdateCheckService.IsNewer(null, "0.3.0.1"), "null tag fails safe");
            Check(!UpdateCheckService.IsNewer("v0.3.0.2", null), "null current fails safe");
            Check(!UpdateCheckService.IsNewer("latest", "0.3.0.1"), "non-numeric tag fails safe");

            // ---- PickSetupAsset ----
            static JsonDocument Assets(params (string Name, string Url, long Size)[] items)
            {
                var json = "{\"assets\":[" + string.Join(",", items.Select(i =>
                    $"{{\"name\":\"{i.Name}\",\"browser_download_url\":\"{i.Url}\",\"size\":{i.Size}}}")) + "]}";
                return JsonDocument.Parse(json);
            }

            using (var doc = Assets(("kaliteConfig-Setup-0.3.0.2.exe", "https://dl/0.3.0.2.exe", 80_000_000)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is not null && picked.DownloadUrl == "https://dl/0.3.0.2.exe",
                    "exact-version asset picked");
            }

            // Stale asset under a new tag must NOT be offered (the loop bug).
            using (var doc = Assets(("kaliteConfig-Setup-0.3.0.1.exe", "https://dl/old.exe", 80_000_000)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is null, "stale asset under a new tag refused (no offer, no loop)");
            }

            // Multi-asset release: newest-version asset wins, not list order.
            using (var doc = Assets(
                ("kaliteConfig-Setup-0.3.0.1.exe", "https://dl/old.exe", 1),
                ("kaliteConfig-Setup-0.3.0.2.exe", "https://dl/new.exe", 2)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is not null && picked.DownloadUrl == "https://dl/new.exe",
                    "multi-asset release resolves by version, not list order");
            }

            // Legacy consumer asset still migrates users forward when it matches.
            using (var doc = Assets(("kaliteConfig-Consumer-Setup-0.3.0.2.exe", "https://dl/cons.exe", 3)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is not null && picked.DownloadUrl == "https://dl/cons.exe",
                    "legacy consumer asset accepted when version-matched");
            }

            // Full flavor preferred over legacy when both match.
            using (var doc = Assets(
                ("kaliteConfig-Consumer-Setup-0.3.0.2.exe", "https://dl/cons.exe", 3),
                ("kaliteConfig-Setup-0.3.0.2.exe", "https://dl/full.exe", 4)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is not null && picked.DownloadUrl == "https://dl/full.exe",
                    "full flavor preferred over legacy consumer asset");
            }

            // Non-exe and unrelated files ignored.
            using (var doc = Assets(
                ("kaliteConfig-Setup-0.3.0.2.zip", "https://dl/x.zip", 5),
                ("checksums.txt", "https://dl/sums.txt", 6)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "v0.3.0.2");
                Check(picked is null, "non-exe files never selected");
            }

            // Empty tag never matches.
            using (var doc = Assets(("kaliteConfig-Setup-0.3.0.2.exe", "https://dl/new.exe", 2)))
            {
                var picked = UpdateCheckService.PickSetupAsset(doc.RootElement.GetProperty("assets"), "");
                Check(picked is null, "empty tag selects nothing");
            }

            Check(UpdateCheckService.BuildSilentInstallerArguments(@"C:\Program Files\kaliteConfig")
                    .Contains("/DIR=\"C:\\Program Files\\kaliteConfig\"", StringComparison.Ordinal),
                "silent args pin registered install directory");
            // /NOCLOSEAPPLICATIONS is the real switch. The old
            // /CLOSEAPPLICATIONS=0 was not: unknown parameters are ignored, so
            // Setup kept its RestartManager behaviour, could not close the
            // tray app, and (Abort by default under /SUPPRESSMSGBOXES) silently
            // rolled the whole upgrade back.
            var noClose = UpdateCheckService.BuildSilentInstallerArguments(null, null);
            Check(noClose.Contains("/NOCLOSEAPPLICATIONS", StringComparison.Ordinal),
                "silent args use the real /NOCLOSEAPPLICATIONS switch");
            Check(!noClose.Contains("CLOSEAPPLICATIONS=0", StringComparison.Ordinal),
                "bogus /CLOSEAPPLICATIONS=0 is gone");
            var logged = UpdateCheckService.BuildSilentInstallerArguments(
                null, @"C:\Users\me\AppData\Local\Temp\kaliteConfig-setup-0.3.0.11.log");
            Check(logged.Contains("/LOG=\"C:\\Users\\me\\AppData\\Local\\Temp\\kaliteConfig-setup-0.3.0.11.log\"",
                    StringComparison.Ordinal),
                "silent args always leave a Setup log");
            Check(!UpdateCheckService.BuildSilentInstallerArguments(null, null)
                    .Contains("/LOG", StringComparison.Ordinal),
                "no /LOG flag when no log path is supplied");

            // ---- pending-attempt classification ----
            // A setup exe was handed to Windows; whether the upgrade landed is
            // decided ONLY by the versions on disk.
            var pendingFailed = new UpdateCheckService.PendingUpdate(
                "0.3.0.10", @"C:\t\kaliteConfig-Setup-0.3.0.10.exe", @"C:\t\setup.log", false);
            Check(UpdateCheckService.ClassifyPendingAttempt(pendingFailed, "0.3.0.9", "0.3.0.9")
                    == UpdateCheckService.UpdateAttemptState.Failed,
                "attempt that changed nothing is Failed");
            Check(UpdateCheckService.ClassifyPendingAttempt(pendingFailed, "0.3.0.10", "0.3.0.10")
                    == UpdateCheckService.UpdateAttemptState.Applied,
                "attempt that landed in this session is Applied");
            Check(UpdateCheckService.ClassifyPendingAttempt(pendingFailed, "0.3.0.9", "0.3.0.10")
                    == UpdateCheckService.UpdateAttemptState.AppliedElsewhere,
                "install took on disk but this session is stale -> AppliedElsewhere");
            Check(UpdateCheckService.ClassifyPendingAttempt(pendingFailed, "0.3.0.11", "0.3.0.11")
                    == UpdateCheckService.UpdateAttemptState.Applied,
                "running newer than the attempt counts as Applied");
            Check(UpdateCheckService.ClassifyPendingAttempt(null, "0.3.0.9", "0.3.0.9")
                    == UpdateCheckService.UpdateAttemptState.None,
                "no pending attempt -> None");
            Check(UpdateCheckService.ClassifyPendingAttempt(pendingFailed, null, null)
                    == UpdateCheckService.UpdateAttemptState.Failed,
                "unreadable versions fail to Failed, never to silence");

            // ---- installer log summariser (real log text from a failed run) ----
            string[] abortLog =
            {
                "2026-09-16 18:38:44.806   Log opened. (Time zone: UTC-07:00)",
                "2026-09-16 18:38:44.853   Found 292 files to register with RestartManager.",
                "2026-09-16 18:38:44.970   RestartManager found an application using one of our files: kaliteConfig",
                "2026-09-16 18:39:15.205   Some applications could not be shut down.",
                "2026-09-16 18:39:15.205   Defaulting to Abort for suppressed message box (Abort/Retry/Ignore):",
                "                          Setup was unable to automatically close all applications. It is recommended that you close all applications using files that need to be updated by Setup before continuing.",
                "2026-09-16 18:39:15.205   User canceled the installation process.",
                "2026-09-16 18:39:15.205   Rolling back changes.",
                "2026-09-16 18:39:15.208   Log closed.",
            };
            var summary = UpdateCheckService.SummarizeInstallerLog(abortLog);
            Check(summary is not null && summary.Contains("unable to automatically close all applications", StringComparison.Ordinal),
                "suppressed-abort log yields Setup's own reason");
            Check(summary is not null && !summary.Contains("2026-09-16", StringComparison.Ordinal),
                "summarised reason has the log timestamp stripped");
            Check(UpdateCheckService.SummarizeInstallerLog(new[]
                    {
                        "2026-09-16 18:39:15.205   Log opened.",
                        "2026-09-16 18:39:15.205   Rolling back changes.",
                    })
                    == "Setup rolled the installation back.",
                "rollback-only log yields the rollback summary");
            Check(UpdateCheckService.SummarizeInstallerLog(new[] { "2026-09-16 18:39:15.205   Log closed." }) is null,
                "clean log yields no failure reason");
            Check(UpdateCheckService.SummarizeInstallerLog(Array.Empty<string>()) is null,
                "empty log yields no failure reason");
            Check(UpdateCheckService.SummarizeInstallerLog(null) is null,
                "missing log yields no failure reason");

            Check(UpdateCheckService.ShouldHandOffToInstalledCopy(
                    "0.3.0.10", "0.3.0.9", "0.3.0.10",
                    @"C:\dev\kaliteConfig.exe", @"C:\Program Files\kaliteConfig\kaliteConfig.exe"),
                "hand off when install dir has pending version");
            Check(!UpdateCheckService.ShouldHandOffToInstalledCopy(
                    "0.3.0.10", "0.3.0.9", "0.3.0.9",
                    @"C:\dev\kaliteConfig.exe", @"C:\Program Files\kaliteConfig\kaliteConfig.exe"),
                "no hand off when registered install is still old");
            Check(!UpdateCheckService.ShouldHandOffToInstalledCopy(
                    "0.3.0.10", "0.3.0.9", "0.3.0.10",
                    @"C:\Program Files\kaliteConfig\kaliteConfig.exe",
                    @"C:\Program Files\kaliteConfig\kaliteConfig.exe"),
                "no hand off when already running installed copy");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
