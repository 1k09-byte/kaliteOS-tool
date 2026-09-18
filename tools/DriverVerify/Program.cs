using System;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace DriverVerify
{
    /// <summary>
    /// Offline checks for the driver-store list: pnputil output parsing
    /// (both Driver Version shapes, missing separators, junk lines) and the
    /// delete guards. Live pnputil is never invoked destructively here.
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
            Console.WriteLine("=== Driver store parsing verification (offline) ===");

            const string sample = @"Microsoft PnP Utility

Published Name :     oem13.inf
Original Name :      nv_dispi.inf
Provider Name :      NVIDIA
Class Name :         Display adapters
Class GUID :         {4d36e968-e325-11ce-bfc1-08002be10318}
Driver Version :     12/05/2024,32.0.15.6616
Signer Name :        Microsoft Windows Hardware Compatibility Publisher

Published Name :     oem7.inf
Original Name :      rtwlanu.inf
Provider Name :      Realtek
Class Name :         Network adapters
Driver Version :     07/11/2024 10.0.22621.3958
Signer Name :        Microsoft Windows Hardware Compatibility Publisher
";
            var items = DriverStoreService.ParseEnumOutput(sample);
            Check(items.Count == 2, $"two blocks parsed (got {items.Count})");
            Check(items[0].PublishedName == "oem13.inf" && items[0].OriginalName == "nv_dispi.inf",
                "published/original names captured");
            Check(items[0].Provider == "NVIDIA" && items[0].ClassName == "Display adapters",
                "provider/class captured");
            Check(items[0].DriverDate == "12/05/2024" && items[0].DriverVersion == "32.0.15.6616",
                $"comma version split (got '{items[0].DriverDate}' / '{items[0].DriverVersion}')");
            Check(items[1].DriverDate == "07/11/2024" && items[1].DriverVersion == "10.0.22621.3958",
                $"space version split (got '{items[1].DriverDate}' / '{items[1].DriverVersion}')");
            Check(items[1].SignerName.Contains("Microsoft Windows Hardware Compatibility"),
                "signer captured");

            // Missing blank separator between blocks.
            var glued = sample.Replace("Signer Name :        Microsoft Windows Hardware Compatibility Publisher\n\nPublished Name", "Signer Name :        X\nPublished Name");
            var gluedItems = DriverStoreService.ParseEnumOutput(glued);
            Check(gluedItems.Count == 2, $"blocks split without blank line (got {gluedItems.Count})");

            // Incomplete block (no published name) is dropped, junk ignored.
            var messy = "Microsoft PnP Utility\nProcessing...\n\nOriginal Name : orphan.inf\nProvider Name : Ghost\n\n" + sample;
            var messyItems = DriverStoreService.ParseEnumOutput(messy);
            Check(messyItems.Count == 2, $"orphan block dropped, headers ignored (got {messyItems.Count})");

            // Empty/null input.
            Check(DriverStoreService.ParseEnumOutput("").Count == 0, "empty output parses to nothing");
            Check(DriverStoreService.ParseEnumOutput("garbage without colons\nmore garbage").Count == 0,
                "junk without keys parses to nothing");

            // Friendly names for regular users (no icon soup, no INF soup).
            var nv = new DriverPackageItem { Provider = "NVIDIA", ClassName = "Display adapters", OriginalName = "nv_dispi.inf", PublishedName = "oem30.inf" };
            Check(nv.FriendlyKind == "NVIDIA display driver", $"friendly kind (got '{nv.FriendlyKind}')");
            Check(nv.Subtitle == "NVIDIA · nv_dispi.inf · oem30.inf", $"subtitle (got '{nv.Subtitle}')");
            var rt = new DriverPackageItem { Provider = "Realtek", ClassName = "Net", OriginalName = "rt640x64.inf", PublishedName = "oem12.inf" };
            Check(rt.FriendlyKind == "Realtek network driver", $"net mapped (got '{rt.FriendlyKind}')");
            var ap = new DriverPackageItem { Provider = "Microsoft", ClassName = "AudioProcessingObject", OriginalName = "x.inf", PublishedName = "oem23.inf" };
            Check(ap.FriendlyClass == "audio", $"jargon class mapped (got '{ap.FriendlyClass}')");
            var hid = new DriverPackageItem { Provider = "Razer Inc", ClassName = "HIDClass", OriginalName = "rz00b2dev.inf", PublishedName = "oem24.inf" };
            Check(hid.FriendlyKind == "Razer Inc input driver", $"hid mapped (got '{hid.FriendlyKind}')");
            var weird = new DriverPackageItem { Provider = "X", ClassName = "QuantumFlux", OriginalName = "q.inf", PublishedName = "oem9.inf" };
            Check(weird.FriendlyClass == "QuantumFlux" && weird.FriendlyKind == "X QuantumFlux driver",
                "unknown class falls back untouched, never mangled");
            var anon = new DriverPackageItem { Provider = "", ClassName = "System", OriginalName = "s.inf", PublishedName = "oem1.inf" };
            Check(anon.FriendlyKind == "System driver", $"empty provider capitalized (got '{anon.FriendlyKind}')");

            // Version splitter edges.
            DriverStoreService.SplitDriverVersion(null, out string d0, out string v0);
            Check(d0 == "" && v0 == "", "null version splits empty");
            DriverStoreService.SplitDriverVersion("32.0.15.6616", out string d1, out string v1);
            Check(d1 == "" && v1 == "32.0.15.6616", "bare version stays whole");

            // Delete guards refuse without ever spawning pnputil.
            var svc = new DriverStoreService();
            string? r1 = svc.DeletePackageAsync(new DriverPackageItem { PublishedName = "" }).GetAwaiter().GetResult();
            Check(r1 is not null && r1.Contains("Refusing"), "empty published name refused");
            string? r2 = svc.DeletePackageAsync(new DriverPackageItem { PublishedName = "mydriver.inf" }).GetAwaiter().GetResult();
            Check(r2 is not null && r2.Contains("oem##.inf"), "non-oem name refused");
            string? r3 = svc.DeletePackageAsync(new DriverPackageItem { PublishedName = "..\\evil.inf" }).GetAwaiter().GetResult();
            Check(r3 is not null && r3.Contains("Refusing"), "path traversal name refused");
            string? r4 = svc.DeletePackageAsync(new DriverPackageItem { PublishedName = "oem999.inf" }).GetAwaiter().GetResult();
            Check(r4 is null || !r4.Contains("Refusing"),
                $"well-formed oem name passes the guard (pnputil result: '{(r4 ?? "OK")}')");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
