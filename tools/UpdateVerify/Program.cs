using System;
using System.Linq;
using System.Text.Json;
using kaliteConfig.Services;

namespace UpdateVerify
{
    /// <summary>
    /// Offline checks for the auto-update loop guards: version comparison and
    /// version-matched asset selection. No network, no GPU — runs anywhere.
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
            Check(UpdateCheckService.BuildSilentInstallerArguments(null)
                    .Contains("/CLOSEAPPLICATIONS=0", StringComparison.Ordinal),
                "silent args disable CloseApplications");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
