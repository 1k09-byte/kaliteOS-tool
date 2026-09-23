using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;

namespace PackageVerify
{
    /// <summary>
    /// Offline checks (table parsing, bundles, CSV, display rules) plus an
    /// opt-in live mode that exercises the real winget.exe read-only paths.
    /// Usage: dotnet run --project tools/PackageVerify [-- live]
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
            Console.WriteLine("=== Package manager verification (offline) ===");

            // Captured live: winget upgrade (note the "< 3.14.7" oddity).
            // Built with fixed column widths so the dash-anchored slicing is
            // exercised exactly like the real full-width tables.
            static string Row(params (string Text, int Width)[] cols) =>
                string.Join(" ", cols.Select(c => c.Text.PadRight(c.Width)));
            static string Dashes(params int[] widths) =>
                string.Join(" ", widths.Select(w => new string('-', w)));

            int[] upgradeWidths = { 60, 30, 20, 20, 10 };
        string upgradeSample =
            "Windows Package Manager v1.29.290\n© 2026 Microsoft. All rights reserved.\n\n" +
            Row(("Name", 60), ("Id", 30), ("Version", 20), ("Available", 20), ("Source", 10)) + "\n" +
            Dashes(upgradeWidths) + "\n" +
            Row(("7-Zip 25.01 (x64)", 60), ("7zip.7zip", 30), ("25.01", 20), ("26.03", 20), ("winget", 10)) + "\n" +
            Row(("Python Launcher", 60), ("Python.Launcher", 30), ("< 3.14.7", 20), ("3.14.7", 20), ("winget", 10)) + "\n" +
            "\n18 upgrades available.\n";

        // Captured live: winget list (ARP/MSIX pseudo-ids, empty Source cells).
        int[] listWidths = { 60, 85, 25, 20, 10 };
        string listSample =
            Row(("Name", 60), ("Id", 85), ("Version", 25), ("Available", 20), ("Source", 10)) + "\n" +
            Dashes(listWidths) + "\n" +
            Row(("App Publisher 1.0.0", 60), (@"ARP\User\X64\48817a26-123a-5a4a-af87-2773b4d33fe1", 85), ("1.0.0", 25), ("", 20), ("", 10)) + "\n" +
            Row(("AV1 Video Extension", 60), (@"MSIX\Microsoft.AV1VideoExtension_2.0.30.0_x64__8wekyb3d8bbwe", 85), ("2.0.30.0", 25), ("", 20), ("", 10)) + "\n" +
            Row(("Claude", 60), ("Anthropic.Claude", 85), ("1.52386.6.0", 25), ("", 20), ("winget", 10)) + "\n";

        // Shape of winget search (no Available column).
        int[] searchWidths = { 25, 20, 10, 10, 10 };
        string searchSample =
            Row(("Name", 25), ("Id", 20), ("Version", 10), ("Match", 10), ("Source", 10)) + "\n" +
            Dashes(searchWidths) + "\n" +
            Row(("7-Zip", 25), ("7zip.7zip", 20), ("26.03", 10), ("Command", 10), ("winget", 10)) + "\n" +
            Row(("7-Zip ZS", 25), ("mcmilk.7zip-zs", 20), ("24.09", 10), ("Tag", 10), ("winget", 10)) + "\n";

            var upgrades = WinGetPackageSource.ParsePackageTable(upgradeSample, "winget", "WinGet");
            Check(upgrades.Count == 2, $"upgrade rows parsed (got {upgrades.Count})");
            if (upgrades.Count == 2)
            {
                Check(upgrades[0].Id == "7zip.7zip" && upgrades[0].Name == "7-Zip 25.01 (x64)",
                    "spaced name + id sliced, not split");
                Check(upgrades[0].InstalledVersion == "25.01" && upgrades[0].AvailableVersion == "26.03",
                    "version/available columns mapped");
                Check(upgrades[1].InstalledVersion == "< 3.14.7" && upgrades[1].HasUpdate,
                    "odd '< 3.14.7' version kept raw, still counts as update");
                Check(upgrades.All(u => u.SourceLabel == "winget"), "source column mapped");
            }

            var installed = WinGetPackageSource.ParsePackageTable(listSample, "winget", "WinGet");
            Check(installed.Count == 3, $"list rows parsed (got {installed.Count})");
            if (installed.Count == 3)
            {
                Check(installed[0].Id == @"ARP\User\X64\48817a26-123a-5a4a-af87-2773b4d33fe1"
                      && installed[0].SourceLabel == "WinGet",
                    "ARP pseudo-id kept whole, empty source falls back to label");
                Check(installed[1].Id.StartsWith(@"MSIX\"), "MSIX id kept whole");
                Check(!installed[0].HasUpdate && !installed[2].HasUpdate, "no Available means no update flag");
            }

            var found = WinGetPackageSource.ParsePackageTable(searchSample, "winget", "WinGet");
            Check(found.Count == 2 && found[0].Id == "7zip.7zip" && found[0].AvailableVersion == "",
                "search shape (no Available column) parses");

            // Compact single-dash-run shape (exact-id queries emit one run).
            string compactSample =
                "Name  Id        Version Source\n" +
                "-------------------------------\n" +
                "7-Zip 7zip.7zip 26.03   winget\n";
            var compact = WinGetPackageSource.ParsePackageTable(compactSample, "winget", "WinGet");
            Check(compact.Count == 1 && compact[0].Id == "7zip.7zip" && compact[0].Name == "7-Zip"
                  && compact[0].InstalledVersion == "26.03" && compact[0].SourceLabel == "winget",
                "compact single-run table falls back to header gaps");
            Check(WinGetPackageSource.ParsePackageTable("No package found matching input criteria.", "w", "W").Count == 0,
                "empty search yields nothing");
            Check(WinGetPackageSource.ParsePackageTable("garbage\nmore garbage", "w", "W").Count == 0,
                "garbage yields nothing (no phantom rows)");

            // Version-not-determinable footer (live shape) must not become a row.
            string unknownFooter =
                Row(("Name", 60), ("Id", 30), ("Version", 20), ("Available", 20), ("Source", 10)) + "\n" +
                Dashes(60, 30, 20, 20, 10) + "\n" +
                Row(("7-Zip 25.01 (x64)", 60), ("7zip.7zip", 30), ("25.01", 20), ("26.03", 20), ("winget", 10)) + "\n" +
                "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\n";
            var footerRows = WinGetPackageSource.ParsePackageTable(unknownFooter, "winget", "WinGet");
            Check(footerRows.Count == 1 && footerRows[0].Id == "7zip.7zip",
                "version-unknown footer skipped, real row kept");

            // Display rules.
            var same = new PackageInfo { InstalledVersion = "1.0", AvailableVersion = "1.0" };
            Check(!same.HasUpdate && same.VersionLine == "1.0", "equal versions: no update, single display");
            var diff = new PackageInfo { InstalledVersion = "1.0", AvailableVersion = "2.0" };
            Check(diff.HasUpdate && diff.VersionLine == "1.0 → 2.0", "differing versions arrow display");
            var availOnly = new PackageInfo { AvailableVersion = "2.0" };
            Check(availOnly.VersionLine == "2.0", "available-only display");
            var neither = new PackageInfo();
            Check(neither.VersionLine == "-" && !neither.HasUpdate, "empty display");
            Check(new PackageInfo { Name = "", Id = "x.y" }.DisplayName == "x.y", "id fallback for empty name");

            // Bundles: round-trip + missing diff.
            string dir = Path.Combine(Path.GetTempPath(), "pkgverify-" + Guid.NewGuid().ToString("N"));
            var bundles = new PackageBundleService(dir);
            var bundle = new PackageBundle { Name = "Essentials" };
            bundle.Items.Add(new BundleItem { PackageId = "7zip.7zip", SourceId = "winget", DisplayName = "7-Zip" });
            bundle.Items.Add(new BundleItem { PackageId = "Git.Git", SourceId = "winget", DisplayName = "Git" });
            bundles.Save(bundle);
            File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{not json");
            var loaded = bundles.LoadAll();
            Check(loaded.Count == 1 && loaded[0].Items.Count == 2, "bundle round-trips, corrupt file ignored");
            var installedNow = new List<PackageInfo>
            {
                new() { Id = "7ZIP.7ZIP", SourceId = "WINGET" },
            };
            var missing = PackageBundleService.DiffMissing(loaded[0], installedNow);
            Check(missing.Count == 1 && missing[0].PackageId == "Git.Git",
                "missing-diff is case-insensitive on id+source");
            Check(PackageBundleService.DiffMissing(loaded[0], new List<PackageInfo>()).Count == 2,
                "empty machine means everything missing");
            var reimport = bundles.ImportFromJson(bundles.ExportToJson(loaded[0]));
            Check(reimport is not null && reimport.Items.Count == 2, "export/import JSON round-trip");
            Check(bundles.ImportFromJson("nope") is null, "bad import rejected");
            try { Directory.Delete(dir, recursive: true); } catch { }

            // CSV quoting.
            var csvRows = new List<PackageInfo>
            {
                new() { Name = "Say \"Hi\" Tool", Id = "x.y", InstalledVersion = "1.0", AvailableVersion = "2.0", SourceLabel = "WinGet", Publisher = "Acme, Inc." },
            };
            string csv = PackageCsvExporter.Build(csvRows);
            Check(csv.StartsWith("Name,Id,Installed,Available,Source,Publisher"),
                "CSV header");
            Check(csv.Contains("\"Say \"\"Hi\"\" Tool\"") && csv.Contains("\"Acme, Inc.\""),
                "CSV quotes embedded quotes and commas");

            if (args.Contains("live", StringComparer.OrdinalIgnoreCase))
                LiveChecks();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Read-only winget.exe paths only - never installs anything.</summary>
        private static void LiveChecks()
        {
            Console.WriteLine("--- live winget (read-only) ---");
            var source = new WinGetPackageSource();
            try
            {
                var status = source.DetectAsync().GetAwaiter().GetResult();
                Check(status.IsDetected && status.DetectedVersion.Length > 0,
                    $"winget detected ({status.DetectedVersion} at {status.ExecutablePath})");
            }
            catch (Exception ex) { Check(false, "detect threw: " + ex.Message); }

            try
            {
                var rows = source.SearchAsync("7-Zip", PackageSearchMode.Exact, default).GetAwaiter().GetResult();
                Check(rows.Any(r => r.Id == "7zip.7zip"), $"exact search finds 7zip.7zip ({rows.Count} rows)");
            }
            catch (Exception ex) { Check(false, "search threw: " + ex.Message); }

            try
            {
                var rows = source.GetAvailableUpdatesAsync(default).GetAwaiter().GetResult();
                Check(rows.All(r => r.AvailableVersion.Length > 0), $"upgrade rows all carry Available ({rows.Count} rows)");
            }
            catch (Exception ex) { Check(false, "upgrade list threw: " + ex.Message); }
        }
    }
}
