using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace kaliteConfig.Services
{
    /// <summary>
    /// Registry-backed install flag for KaliteOS.
    /// Path: HKLM\SOFTWARE\KaliteOS  Value: IsInstalled (REG_DWORD 0/1)
    /// Fallback: HKCU\SOFTWARE\KaliteOS when HKLM is not writable / not elevated.
    /// Semantics:
    ///   0 (or missing) → first launch / needs Windhawk auto-provisioning
    ///   1             → already provisioned, do nothing on launch
    /// </summary>
    public static class KaliteOSRegistryService
    {
        public const string SubKeyPath = @"SOFTWARE\KaliteOS";
        public const string ValueName = "IsInstalled";

        private static RegistryKey OpenBase(RegistryHive hive, RegistryView view, bool writable)
        {
            return RegistryKey.OpenBaseKey(hive, view);
        }

        /// <summary>
        /// Returns the current IsInstalled value. Missing key/value → 0 (needs install).
        /// Checks HKLM 64-bit first, then HKLM 32-bit, then HKCU as fallback.
        /// </summary>
        public static int GetIsInstalled()
        {
            // Try HKLM 64-bit
            int? hkml64 = TryRead(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (hkml64.HasValue) return hkml64.Value;

            // Try HKLM 32-bit (WOW64)
            int? hkml32 = TryRead(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (hkml32.HasValue) return hkml32.Value;

            // Fallback to HKCU
            int? hkcu = TryRead(RegistryHive.CurrentUser, RegistryView.Default);
            if (hkcu.HasValue) return hkcu.Value;

            return 0; // default: not installed -> trigger auto-install
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
            catch (Exception ex)
            {
                Debug.WriteLine($"KaliteOSRegistryService.TryRead {hive}/{view}: {ex.Message}");
                return null;
            }
        }

        /// <summary>True when the registry says we still need to auto-install (0 or missing).</summary>
        public static bool ShouldAutoInstall => GetIsInstalled() == 0;

        /// <summary>
        /// Writes IsInstalled to HKLM\SOFTWARE\KaliteOS. If that fails due to
        /// elevation/permission, falls back to HKCU. Creates the key if needed.
        /// </summary>
        public static void SetIsInstalled(int value)
        {
            value = value != 0 ? 1 : 0;
            // Preferred: HKLM 64-bit
            if (TryWrite(RegistryHive.LocalMachine, RegistryView.Registry64, value)) return;
            // Fallback: HKLM 32-bit
            if (TryWrite(RegistryHive.LocalMachine, RegistryView.Registry32, value)) return;
            // Last resort: HKCU
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
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"KaliteOSRegistryService.TryWrite {hive}/{view} unauthorized: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KaliteOSRegistryService.TryWrite {hive}/{view}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ensures the key exists with a default value (used by installer).</summary>
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
