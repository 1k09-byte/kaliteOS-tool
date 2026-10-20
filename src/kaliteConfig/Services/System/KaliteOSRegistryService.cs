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

namespace kaliteConfig.Services
{
    public static class KaliteOSRegistryService
    {
        public const string SubKeyPath = @"SOFTWARE\KaliteOS";
        public const string ValueName = "IsInstalled";

        public static int GetIsInstalled()
        {
            int? hkml64 = TryRead(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (hkml64.HasValue) return hkml64.Value;

            int? hkml32 = TryRead(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (hkml32.HasValue) return hkml32.Value;

            int? hkcu = TryRead(RegistryHive.CurrentUser, RegistryView.Default);
            if (hkcu.HasValue) return hkcu.Value;

            return 0;
        }

        private static int? TryRead(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var subKey = baseKey.OpenSubKey(SubKeyPath, writable: false);
                if (subKey is null) return null;
                var raw = subKey.GetValue(ValueName);
                if (raw is null) return null;
                if (raw is int i) return i;
                if (raw is byte[] || raw is string)
                {
                    if (int.TryParse(raw.ToString(), out int parsed)) return parsed;
                }
                try { return Convert.ToInt32(raw); } catch { }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool ShouldAutoInstall => GetIsInstalled() == 0;

        public static void SetIsInstalled(int value)
        {
            value = value != 0 ? 1 : 0;
            if (TryWrite(RegistryHive.LocalMachine, RegistryView.Registry64, value)) return;
            if (TryWrite(RegistryHive.LocalMachine, RegistryView.Registry32, value)) return;
            TryWrite(RegistryHive.CurrentUser, RegistryView.Default, value);
        }

        private static bool TryWrite(RegistryHive hive, RegistryView view, int value)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var subKey = baseKey.CreateSubKey(SubKeyPath, writable: true);
                if (subKey is null) return false;
                subKey.SetValue(ValueName, value, RegistryValueKind.DWord);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void EnsureExists(int defaultValue = 0)
        {
            var current = TryRead(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (current.HasValue) return;
            current = TryRead(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (current.HasValue) return;
            current = TryRead(RegistryHive.CurrentUser, RegistryView.Default);
            if (current.HasValue) return;
            SetIsInstalled(defaultValue);
        }
    }
}
