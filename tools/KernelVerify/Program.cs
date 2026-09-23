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

            string notElevated = WindowsSettingsService.AccessDeniedMessage(
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel\ThreadDpcEnable",
                elevated: false, osError: "Access is denied.");
            Check(notElevated.Contains("Administrator rights are required"),
                "unelevated denial asks for admin");
            Check(!notElevated.Contains("HKLM"),
                "unelevated denial keeps it short (no key dump)");

            string elevated = WindowsSettingsService.AccessDeniedMessage(
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel\ThreadDpcEnable",
                elevated: true, osError: "Access is denied.");
            Check(elevated.Contains(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel\ThreadDpcEnable"),
                "elevated denial names the exact write target");
            Check(elevated.Contains("Access is denied."), "elevated denial carries the OS error");
            Check(elevated.Contains("IS running"),
                "elevated denial states the process IS elevated (no false admin lecture)");
            Check(!elevated.Contains("Restart kaliteConfig as administrator"),
                "elevated denial never tells an admin to run as admin");

            string powerDenied = WindowsSettingsService.AccessDeniedMessage(
                "power setting 'Lock interrupt routing' [2bfc24f9-5ea2-4801-8213-3dbae01aa39d] (AC + DC) on the active power scheme",
                elevated: true, osError: "Win32 error 5 writing AC index.");
            Check(powerDenied.Contains("2bfc24f9-5ea2-4801-8213-3dbae01aa39d") && powerDenied.Contains("Win32 error 5"),
                "power-setting denial names the setting GUID and the Win32 error");

            bool threw = false;
            bool isElevated = false;
            try { isElevated = WindowsSettingsService.IsElevated(); }
            catch { threw = true; }
            Check(!threw, $"elevation probe never throws (this machine: {(isElevated ? "elevated" : "not elevated")})");

            Check(WindowsSettingsService.Find("ThreadedDpc") is not null, "Find resolves ThreadedDpc");
            Check(WindowsSettingsService.Find("NoSuchTweak") is null, "Find returns null for unknown id");
            Check(WindowsSettingsService.All.Count == 3, "tweak catalog intact (3 defs)");

            // Location correctness: every tweak must point at a real,
            // Windows-honored setting - never a placebo path.
            var dpc = WindowsSettingsService.Find("ThreadedDpc")!;
            Check(!dpc.IsPowerSetting && dpc.ValueName == "ThreadDpcEnable"
                  && dpc.SubKey.EndsWith(@"Session Manager\Kernel") && dpc.NeedsReboot,
                "ThreadedDpc targets the documented ThreadDpcEnable value (boot-read)");
            var timer = WindowsSettingsService.Find("TimerExpiration")!;
            Check(!timer.IsPowerSetting && timer.ValueName == "SerializeTimerExpiration"
                  && timer.SubKey.EndsWith(@"Session Manager\Kernel") && timer.NeedsReboot,
                "TimerExpiration targets Session Manager\\Kernel (boot-read), not the power scheme");
            var irq = WindowsSettingsService.Find("InterruptRouting")!;
            Check(irq.IsPowerSetting
                  && irq.PowerSubgroup == new Guid("48672f38-7a9a-4bb2-8bf8-3d85be19de4e")
                  && irq.PowerSetting == new Guid("2bfc24f9-5ea2-4801-8213-3dbae01aa39d")
                  && irq.OnValue == 4 && irq.OffValue == 0 && !irq.NeedsReboot,
                "InterruptRouting is the Interrupt Steering Mode setting (on=Lock=4, live, no reboot)");

            // Live read-only end-to-end: the PowrProf declarations must
            // actually work on a real box (a marshaling mistake here would
            // fault). No writes - detection only.
            var svc = new WindowsSettingsService();
            bool threwLive = false;
            WindowsSettingsService.TweakDetection liveIrq = default;
            try { liveIrq = svc.Detect(WindowsSettingsService.Find("InterruptRouting")!); }
            catch { threwLive = true; }
            Check(!threwLive, "live Detect(InterruptRouting) through PowrProf doesn't throw");
            Check(Enum.IsDefined(liveIrq.State), "live detection yields a defined state");
            Check(!liveIrq.RawValue.HasValue || liveIrq.RawValue.Value <= 6,
                $"live AC index in the documented 0-6 range (got {liveIrq.RawValue?.ToString() ?? "null"})");

            bool threwDpc = false;
            try { svc.Detect(WindowsSettingsService.Find("ThreadedDpc")!); }
            catch { threwDpc = true; }
            Check(!threwDpc, "live Detect(ThreadedDpc) registry read doesn't throw");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
