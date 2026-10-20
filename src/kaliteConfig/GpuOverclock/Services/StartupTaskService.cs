using System;
using System.Diagnostics;
using System.IO;

namespace kaliteConfig.GpuOverclock.Services
{
    public sealed class StartupTaskService
    {
        public const string TaskName = "kaliteConfig GPU Overclock";

        public bool IsRegistered
        {
            get
            {
                try
                {
                    var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi)!;
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool Register()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

                var args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --apply-overclock-startup\" " +
                           "/sc onlogon /rl highest /f";
                return RunSchtasks(args);
            }
            catch
            {
                return false;
            }
        }

        public bool Unregister()
        {
            return RunSchtasks($"/delete /tn \"{TaskName}\" /f");
        }

        private static bool RunSchtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
