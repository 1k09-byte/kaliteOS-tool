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
using System;
using System.Collections.Generic;
using System.IO;

namespace kaliteConfig.Services
{
    public static class SystemVerificationService
    {
        private sealed record ExpectedValue(string SubKey, string Name, string Expected, RegistryView View);

        public static string RegFilePath =>
            Path.Combine(AppContext.BaseDirectory, "Assets", "verify.reg");

        private static readonly ExpectedValue[] Fallback =
        {
            new(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionSubVersion", "KaliteOS", RegistryView.Registry64),
            new(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization", "Kalite", RegistryView.Registry64),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Manufacturer", "Kal", RegistryView.Registry64),
            new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model", "KaliteOS", RegistryView.Registry64),
        };

        private static readonly ExpectedValue[] Required = LoadFromRegFile();

        public static bool IsVerified(out string detail)
        {
            var missing = new List<string>();
            foreach (var req in Required)
            {
                var actual = TryReadString(req.SubKey, req.Name, req.View);
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

        public static bool IsVerified() => IsVerified(out _);

        private static ExpectedValue[] LoadFromRegFile()
        {
            var values = new List<ExpectedValue>();
            try
            {
                string path = RegFilePath;
                if (!File.Exists(path)) return values.ToArray();

                const string lm64 = "HKEY_LOCAL_MACHINE";
                const string lm32 = "HKEY_LOCAL_MACHINE_32";

                string? subKey = null;
                var view = RegistryView.Registry64;

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' ||
                        line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (line[0] == '[' && line[^1] == ']')
                    {
                        var header = line[1..^1];
                        view = RegistryView.Registry64;
                        if (header.StartsWith(lm32, StringComparison.OrdinalIgnoreCase))
                        {
                            subKey = header[lm32.Length..].TrimStart('\\');
                            view = RegistryView.Registry32;
                        }
                        else if (header.StartsWith(lm64, StringComparison.OrdinalIgnoreCase))
                        {
                            subKey = header[lm64.Length..].TrimStart('\\');
                        }
                        else
                        {
                            subKey = null;
                        }
                        continue;
                    }

                    if (subKey is null) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var name = Unquote(line[..eq].Trim());
                    if (name.Length == 0) continue;
                    values.Add(new ExpectedValue(subKey, name, Unquote(line[(eq + 1)..].Trim()), view));
                }
            }
            catch
            {
            }

            return values.Count > 0 ? values.ToArray() : Fallback;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
            return s;
        }

        private static string? TryReadString(string subKey, string name, RegistryView preferred)
        {
            if (TryReadOne(RegistryHive.LocalMachine, preferred, subKey, name) is { } first && first.found)
                return first.value;
            var other = preferred == RegistryView.Registry64 ? RegistryView.Registry32 : RegistryView.Registry64;
            if (TryReadOne(RegistryHive.LocalMachine, other, subKey, name) is { } second && second.found)
                return second.value;
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