using System;
using kaliteConfig.Services;

namespace KernelVerify
{
    /// <summary>
    /// Offline checks for the kernel-tweak access-denied reporting: the
    /// message must say "run as admin" ONLY when genuinely unelevated, and
    /// otherwise name the key, the OS error, and the likely culprits.
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
            Console.WriteLine("=== Kernel tweak denial-message verification (offline) ===");

            string notElevated = KernelTuningService.AccessDeniedMessage(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
                "ThreadedDpcEnable", elevated: false, osError: "Access is denied.");
            Check(notElevated.Contains("Administrator rights are required"),
                "unelevated denial asks for admin");
            Check(!notElevated.Contains("HKLM"),
                "unelevated denial keeps it short (no key dump)");

            string elevated = KernelTuningService.AccessDeniedMessage(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Kernel",
                "ThreadedDpcEnable", elevated: true, osError: "Access is denied.");
            Check(elevated.Contains(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel"),
                "elevated denial names the exact key path");
            Check(elevated.Contains("ThreadedDpcEnable"), "elevated denial names the value");
            Check(elevated.Contains("Access is denied."), "elevated denial carries the OS error");
            Check(elevated.Contains("IS running"),
                "elevated denial states the process IS elevated (no false admin lecture)");
            Check(!elevated.Contains("Restart kaliteConfig as administrator"),
                "elevated denial never tells an admin to run as admin");

            bool threw = false;
            bool isElevated = false;
            try { isElevated = KernelTuningService.IsElevated(); }
            catch { threw = true; }
            Check(!threw, $"elevation probe never throws (this machine: {(isElevated ? "elevated" : "not elevated")})");

            Check(KernelTuningService.Find("ThreadedDpc") is not null, "Find resolves ThreadedDpc");
            Check(KernelTuningService.Find("NoSuchTweak") is null, "Find returns null for unknown id");
            Check(KernelTuningService.All.Count == 3, "tweak catalog intact (3 defs)");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
