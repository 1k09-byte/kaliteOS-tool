// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Registers/unregisters an elevated Task Scheduler task ("kaliteConfig GPU
    /// Overclock") that reapplies the designated startup profile shortly after
    /// login - NVAPI state does not survive reboots or driver reloads.
    /// The UI must always show IsRegistered, so registration state never drifts
    /// silently out of sync with the system.
    /// </summary>
    public sealed class StartupTaskService
    {
        public const string TaskName = "kaliteConfig GPU Overclock";

        /// <summary>True when the task exists in Task Scheduler (regardless of state).</summary>
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

        /// <summary>
        /// Creates the task: at logon, elevated, starting the app with
        /// --apply-overclock-startup. The task runs as the current (admin)
        /// user since the whole app requires elevation.
        /// </summary>
        public bool Register()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

                // /RL HIGHEST = run elevated. schtasks lacks an XML-less way to set
                // "run with highest privileges" + logon trigger together except /sc onlogon.
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
