// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.Win32;
using System.Collections.Generic;

namespace kaliteConfig.Services
{
    /// <summary>
    /// KaliteOS system verification gate (verify.reg).
    ///
    /// The toolkit only runs on a KaliteOS-stamped system. Expected values
    /// (see verify.reg):
    ///   HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion
    ///     EditionSubVersion = "KaliteOS"
    ///     RegisteredOrganization = "Kalite"
    ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation
    ///     Manufacturer = "Kal"
    ///     Model = "KaliteOS"
    ///     SupportHours = "" (must exist, empty)
    ///     SupportURL = "" (must exist, empty)
    ///
    /// Reads HKLM 64-bit first, then 32-bit (WOW64) so x86 builds see the
    /// real keys. All values must match exactly (ordinal); any missing or
    /// mismatched value fails verification.
    /// </summary>
    public static class SystemVerificationService
    {
        private sealed record ExpectedValue(string SubKey, string Name, string Expected);

        private static readonly ExpectedValue[] Required =
        {
            new(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionSubVersion", "KaliteOS"),
            new(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization", "Kalite"),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer", "Kal"),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model", "KaliteOS"),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportHours", ""),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "SupportURL", ""),
        };

        /// <summary>True when every required value exists with the expected data.</summary>
        public static bool IsVerified(out string detail)
        {
            var missing = new List<string>();
            foreach (var req in Required)
            {
                var actual = TryReadString(req.SubKey, req.Name);
                if (actual is null)
                {
                    missing.Add($"{req.SubKey}\\{req.Name} (missing)");
                }
                else if (actual != req.Expected)
                {
                    missing.Add($"{req.SubKey}\\{req.Name} (expected \"{req.Expected}\", found \"{actual}\")");
                }
            }

            if (missing.Count == 0)
            {
                detail = "";
                return true;
            }

            detail = string.Join("\n", missing);
            return false;
        }

        /// <summary>True when every required value exists with the expected data.</summary>
        public static bool IsVerified() => IsVerified(out _);

        private static string? TryReadString(string subKey, string name)
        {
            // HKLM 64-bit first (real location on 64-bit Windows).
            var v64 = TryReadOne(RegistryHive.LocalMachine, RegistryView.Registry64, subKey, name);
            if (v64.found) return v64.value;
            // WOW64 fallback for 32-bit builds.
            var v32 = TryReadOne(RegistryHive.LocalMachine, RegistryView.Registry32, subKey, name);
            if (v32.found) return v32.value;
            return null;
        }

        private static (bool found, string? value) TryReadOne(
            RegistryHive hive, RegistryView view, string subKey, string name)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey, writable: false);
                if (key is null) return (false, null);
                var raw = key.GetValue(name);
                if (raw is null) return (false, null);
                // REG_SZ / REG_EXPAND_SZ come back as string; anything else
                // (DWORD etc.) can never equal the expected string data.
                if (raw is string s) return (true, s);
                return (true, raw.ToString());
            }
            catch
            {
                return (false, null);
            }
        }
    }
}
