// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Diagnostics;

namespace kaliteConfig.Services
{
    public sealed class ReservedCpuSetsService
    {
        private const string KernelSessionKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
        private const string ValueName = "ReservedCpuSets";
        
        private const string RunKeyPath = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "kaliteConfig_ReservedCpuSets";

        // Desired mask, so a startup launch can re-assert the reservation even
        // when the OS dropped the live kernel value across a reboot.
        private const string DesiredKeyPath = @"SOFTWARE\kaliteConfig";
        private const string DesiredValueName = "ReservedCpuSetsDesiredMask";

        public ulong? GetReservedCpuMask()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KernelSessionKey, false);
                if (key == null) return null;

                var value = key.GetValue(ValueName);
                if (value is not byte[] bytes || bytes.Length == 0)
                {
                    return null;
                }

                // Make sure we have safely at least 8 bytes, padding if short
                byte[] safeBytes = new byte[8];
                Array.Copy(bytes, safeBytes, Math.Min(bytes.Length, 8));
                
                // Read directly as a ulong (BitConverter expects little-endian, matching the OS representation).
                ulong mask = BitConverter.ToUInt64(safeBytes, 0);
                return mask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read ReservedCpuSets: {ex.Message}");
                return null;
            }
        }

        public void SetReservedCpuMask(ulong mask)
        {
            if (mask == 0)
            {
                ClearReservedCpuMask();
                return;
            }

            using var key = Registry.LocalMachine.CreateSubKey(KernelSessionKey, true);
            if (key == null)
            {
                throw new UnauthorizedAccessException("Cannot open kernel Session Manager key. Run as Administrator.");
            }

            // Convert to byte array (8 bytes, little endian)
            byte[] bytes = BitConverter.GetBytes(mask);
            key.SetValue(ValueName, bytes, RegistryValueKind.Binary);

            using var desiredKey = Registry.LocalMachine.CreateSubKey(DesiredKeyPath, true);
            desiredKey?.SetValue(DesiredValueName, mask.ToString("X16"), RegistryValueKind.String);
        }

        public ulong? GetDesiredMask()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(DesiredKeyPath, false);
                var value = key?.GetValue(DesiredValueName) as string;
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out ulong mask))
                    return mask;
            }
            catch { }
            return null;
        }

        public void ClearReservedCpuMask()
        {
            using var key = Registry.LocalMachine.CreateSubKey(KernelSessionKey, true);
            if (key == null)
            {
                throw new UnauthorizedAccessException("Cannot open kernel Session Manager key. Run as Administrator.");
            }

            if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }

            try
            {
                using var desiredKey = Registry.LocalMachine.OpenSubKey(DesiredKeyPath, true);
                if (desiredKey?.GetValue(DesiredValueName) != null)
                    desiredKey.DeleteValue(DesiredValueName, false);
            }
            catch { }
        }

        public bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public bool RequiresPerBootReapply()
        {
            try
            {
                // Check HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
                if (key != null)
                {
                    var buildValue = key.GetValue("CurrentBuildNumber") as string;
                    if (int.TryParse(buildValue, out int build))
                    {
                        // 19044 is Windows 10 21H2. Older builds don't persist the kernel flag beyond boot.
                        return build < 19044;
                    }
                }
            }
            catch
            {
                // Ignored - safely assume normal operation if parsing fails
            }
            return false;
        }

        public bool GetApplyAtStartup()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, false);
                if (key != null)
                {
                    var value = key.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch { }
            return false;
        }

        public void SetApplyAtStartup(bool enabled)
        {
            using var key = Registry.LocalMachine.CreateSubKey(RunKeyPath, true);
            if (key == null)
            {
                throw new UnauthorizedAccessException("Cannot open Run key. Run as Administrator.");
            }

            if (enabled)
            {
                // Point to the executable with a silent argument.
                // Assuming the main EXECUTABLE is mapping correctly via Process.GetCurrentProcess().MainModule
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Quote the path and append argument
                    string command = $"\"{exePath}\" --apply-reserved-cpus";
                    key.SetValue(RunValueName, command, RegistryValueKind.String);
                }
            }
            else
            {
                if (key.GetValue(RunValueName) != null)
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }
    }
}
