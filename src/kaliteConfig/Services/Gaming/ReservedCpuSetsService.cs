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

        private const string ApplyAtStartupValueName = "ReservedCpuSetsApplyAtStartup";

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

                byte[] safeBytes = new byte[8];
                Array.Copy(bytes, safeBytes, Math.Min(bytes.Length, 8));

                ulong mask = BitConverter.ToUInt64(safeBytes, 0);
                return mask;
            }
            catch (Exception)
            {
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
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
                if (key != null)
                {
                    var buildValue = key.GetValue("CurrentBuildNumber") as string;
                    if (int.TryParse(buildValue, out int build))
                    {
                        return build < 19044;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public bool GetApplyAtStartup()
        {
            RemoveLegacyRunEntry();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(DesiredKeyPath, false);
                if (key != null)
                {
                    var value = key.GetValue(ApplyAtStartupValueName);
                    return value is int i && i != 0;
                }
            }
            catch { }
            return false;
        }

        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, true);
                key?.DeleteValue(RunValueName, false);
            }
            catch { }
        }

        public void SetApplyAtStartup(bool enabled)
        {
            RemoveLegacyRunEntry();
            using var key = Registry.LocalMachine.CreateSubKey(DesiredKeyPath, true)
                ?? throw new UnauthorizedAccessException("Cannot write settings key. Run as Administrator.");
            key.SetValue(ApplyAtStartupValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
