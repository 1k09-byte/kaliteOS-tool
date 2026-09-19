using System;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;

namespace InstallVerify
{
    /// <summary>
    /// Verifies the Install tab data layer: the REAL InstallerViewModel must
    /// expose non-empty Visible* collections (or explicitly report
    /// NothingToInstall). Exit code 0 = pass.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" + (detail.Length > 0 ? $" ({detail})" : ""));
            if (!ok) _failures++;
        }

        private static int Main()
        {
            Console.WriteLine("=== InstallVerify (REAL InstallerViewModel) ===");
            InstallerViewModel vm;
            try
            {
                vm = new InstallerViewModel(new InstallerService());
                Check(true, "ViewModel constructed");
            }
            catch (Exception ex)
            {
                Check(false, "ViewModel constructed", ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine("=== 1 CHECK(S) FAILED ===");
                return 1;
            }

            Check(vm.Browsers.Count == 4, "browsers seeded", vm.Browsers.Count.ToString());
            Check(vm.GameLaunchers.Count == 6, "launchers seeded", vm.GameLaunchers.Count.ToString());
            Check(vm.SocialApps.Count == 3, "social apps seeded", vm.SocialApps.Count.ToString());
            Check(vm.Utilities.Count >= 1, "utilities seeded", vm.Utilities.Count.ToString());

            int visible = vm.VisibleBrowsers.Count + vm.VisibleGameLaunchers.Count
                + vm.VisibleSocialApps.Count + vm.VisibleUtilities.Count;
            Console.WriteLine($"  visible: browsers={vm.VisibleBrowsers.Count} launchers={vm.VisibleGameLaunchers.Count} " +
                              $"social={vm.VisibleSocialApps.Count} utils={vm.VisibleUtilities.Count} " +
                              $"NothingToInstall={vm.NothingToInstall}");
            Check(visible > 0 || vm.NothingToInstall, "page has cards or honest empty-state");

            vm.ShowInstalled = true;
            int total = vm.Browsers.Count + vm.GameLaunchers.Count + vm.SocialApps.Count + vm.Utilities.Count;
            int shown = vm.VisibleBrowsers.Count + vm.VisibleGameLaunchers.Count
                + vm.VisibleSocialApps.Count + vm.VisibleUtilities.Count;
            Check(shown == total, "ShowInstalled reveals everything", $"{shown}/{total}");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }
    }
}
