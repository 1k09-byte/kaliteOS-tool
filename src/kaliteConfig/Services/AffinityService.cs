// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using kaliteConfig.Models;
using kaliteConfig.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    /// <summary>
    /// Read-only device inventory for the affinity page (layout pass: no system
    /// changes). Walks HKLM\SYSTEM\CurrentControlSet\Enum\PCI - graphics, NIC,
    /// USB and audio controllers are all PCI devices - and keeps entries whose
    /// setup class is Display, Net, USB or MEDIA. IRQ/affinity policy writes
    /// land here in the tuning pass.
    /// </summary>
    public class AffinityService
    {
        // PCI covers GPU/NIC/USB controllers; HD Audio codecs enumerate under
        // HDAUDIO; USB audio gear and USB NIC dongles enumerate under USB
        // (hubs and peripherals are filtered by class below).
        private static readonly string[] EnumRoots = new[]
        {
            @"SYSTEM\CurrentControlSet\Enum\PCI",
            @"SYSTEM\CurrentControlSet\Enum\HDAUDIO",
            @"SYSTEM\CurrentControlSet\Enum\USB"
        };

        public Task<List<AffinityDeviceItem>> EnumerateDevicesAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var result = new List<AffinityDeviceItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var root in EnumRoots)
                    {
                        using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                        if (baseKey is null) continue;
                        foreach (var device in baseKey.GetSubKeyNames())
                        {
                            ct.ThrowIfCancellationRequested();
                            using var deviceKey = baseKey.OpenSubKey(device);
                            if (deviceKey is null) continue;
                            foreach (var instance in deviceKey.GetSubKeyNames())
                            {
                                ct.ThrowIfCancellationRequested();
                                using var key = deviceKey.OpenSubKey(instance);
                                if (key is null) continue;

                                // Skip phantom entries (hardware no longer present -
                                // stale reinstall leftovers that inflate device counts).
                                if (key.GetValue("Phantom") is int phantom && phantom == 1) continue;

                                // DeviceInstanceId keeps the FULL path ("PCI\VEN_…\instance"),
                                // exactly like the reference tool's PnpDeviceId - every
                                // registry read/write builds Enum\{id}\… straight from it.
                                string busPrefix = root.Substring(root.LastIndexOf('\\') + 1);
                                string instanceId = busPrefix + "\\" + device + "\\" + instance;
                                // The 'Phantom' value is unreliable - a disabled iGPU
                                // (e.g. AMD Radeon Graphics while a dGPU drives the
                                // display) keeps its Enum key with no Phantom value but
                                // isn't in the live devnode tree. Ask cfgmgr32 directly.
                                if (!NativeMethods.CfgMgr32.IsDevicePresent(instanceId)) continue;

                                // ClassGUID usually lives on the instance; fall back to the
                                // parent device key so real devices are never dropped.
                                string classGuid = (key.GetValue("ClassGUID") as string ?? "").Trim();
                                if (string.IsNullOrEmpty(classGuid))
                                    classGuid = (deviceKey.GetValue("ClassGUID") as string ?? "").Trim();
                            string? category = MapClass(classGuid);
                            if (category is null) continue;
                            // USB hubs/functions aren't controllers: from the USB bus
                            // only audio gear (DACs/headsets) and NIC dongles count.
                            if (root.EndsWith(@"\USB", StringComparison.OrdinalIgnoreCase) &&
                                category != "Graphics" && category != "Network" && category != "Audio")
                                continue;

                                string rawName = (key.GetValue("DeviceDesc") as string ?? "").Trim();
                                string name = FriendlyName(rawName);
                                if (string.IsNullOrEmpty(name) || IsVirtualAdapter(name)) continue;
                                // Dedupe by instance path, never by name: identical
                                // adapters (e.g. two matching USB controllers) are
                                // distinct tunable devices.
                                if (!seen.Add(instanceId)) continue;

                                result.Add(new AffinityDeviceItem
                                {
                                    Name = name,
                                    Category = category,
                                    DeviceInstanceId = instanceId
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"EnumerateDevices: {ex.Message}");
                }
                DisambiguateDuplicates(result);
                return result;
            }, ct);
        }

        private static string? MapClass(string classGuid)
        {
            string g = classGuid.Trim('{', '}', ' ', '\t').ToLowerInvariant();
            return g switch
            {
                "4d36e968-e325-11ce-bfc1-08002be10318" => "Graphics",
                "4d36e972-e325-11ce-bfc1-08002be10318" => "Network",
                "36fc9e60-c465-11cf-8056-444553540000" => "Usb",
                "4d36e96c-e325-11ce-bfc1-08002be10318" => "Audio",
                _ => null
            };
        }

        // Virtual/host adapters (Hyper-V, VMware, ...) and generic placeholders
        // ("Basic Display", "Remote Display") have no tunable interrupt policy.
        private static bool IsVirtualAdapter(string name)
        {
            return name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Parallels", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Citrix", StringComparison.OrdinalIgnoreCase)
                || name.Contains("QEMU", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VirtIO", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Red Hat", StringComparison.OrdinalIgnoreCase)
                || name.Contains("VMBus", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
        }

        // Identical adapters (e.g. four "USB xHCI Compliant Host Controller"
        // instances) are distinct tunable devices but indistinguishable by name,
        // so later duplicates get a vendor suffix ("(AMD)") or a #N fallback -
        // the same convention Device Manager uses.
        private static void DisambiguateDuplicates(List<Models.AffinityDeviceItem> devices)
        {
            foreach (var group in devices.GroupBy(d => d.Name).Where(g => g.Count() > 1))
            {
                bool first = true;
                int n = 1;
                foreach (var item in group)
                {
                    if (first) { first = false; continue; }
                    n++;
                    string vendor = VendorFromId(item.DeviceInstanceId);
                    item.Name = string.IsNullOrEmpty(vendor)
                        ? $"{item.Name} #{n}"
                        : $"{item.Name} ({vendor})";
                }
            }
        }

        private static string VendorFromId(string instanceId)
        {
            if (instanceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
            if (instanceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return "AMD";
            if (instanceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return "Intel";
            if (instanceId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase)) return "Realtek";
            if (instanceId.Contains("VEN_14F1", StringComparison.OrdinalIgnoreCase)) return "Conexant";
            return string.Empty;
        }

        /// <summary>
        /// Read-only snapshot of a device's interrupt state (same keys MSI-utility
        /// tools display). Missing keys yield nulls - the UI renders "-".
        /// Never writes; the tuning pass adds the writers.
        /// </summary>
        public sealed record InterruptInfo(
            bool? MsiSupported,
            uint? MsiLimit,
            uint? MaxMsiLimit,
            int? DevicePolicy,
            int? DevicePriority,
            ulong? AffinityMask);

        public InterruptInfo GetInterruptInfo(string deviceInstanceId)
        {
            bool? msi = null;
            uint? limit = null;
            uint? maxLimit = null;
            int? policy = null;
            int? priority = null;
            ulong? mask = null;
            try
            {
                string mgmt = $@"SYSTEM\CurrentControlSet\Enum\{deviceInstanceId}\Device Parameters\Interrupt Management";
                using var msiKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(mgmt + @"\MessageSignaledInterruptProperties");
                if (msiKey?.GetValue("MSISupported") is int ms) msi = ms != 0;
                if (msiKey?.GetValue("MessageNumberLimit") is int lim) limit = unchecked((uint)lim);
                if (msiKey?.GetValue("MaxMessageNumberLimit") is int mlim) maxLimit = unchecked((uint)mlim);
                using var affKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(mgmt + @"\Affinity Policy");
                if (affKey?.GetValue("DevicePolicy") is int dp) policy = dp;
                if (affKey?.GetValue("DevicePriority") is int dpr) priority = dpr;
                // The registry MaxMessageNumberLimit is usually absent - fall back
                // to the SetupDi device property, exactly like the reference tool.
                // That property is elevation-gated, so a last-known-good cache
                // (written by elevated runs) covers non-elevated scans.
                if (maxLimit is null or 0)
                {
                    maxLimit = GetSetupDiMaxMsiLimit(deviceInstanceId);
                    if (maxLimit is > 0)
                        StoreMaxMsiCache(deviceInstanceId, maxLimit.Value);
                    else
                        maxLimit = GetCachedMaxMsiLimit(deviceInstanceId);
                }
                if (affKey?.GetValue("AssignmentSetOverride") is byte[] bytes && bytes.Length > 0)
                {
                    byte[] eight = new byte[8];
                    Buffer.BlockCopy(bytes, 0, eight, 0, Math.Min(bytes.Length, 8));
                    mask = BitConverter.ToUInt64(eight, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetInterruptInfo: {ex.Message}");
            }
            return new InterruptInfo(msi, limit, maxLimit, policy, priority, mask);
        }

        // Values match the reference tool exactly: policy 3 is
        // IrqPolicyAllProcessorsInMachine, and priorities are
        // 0 = Undefined, 1 = Low, 2 = Normal, 3 = High.
        // A missing key means 0 (OS default), exactly like the reference
        // tool whose DeviceInfo initializes the policy to 0.
        /// <summary>
        /// Hardware maximum MSI messages via SetupDi + DEVPKEY_PciDevice_
        /// InterruptMessageMaximum - the reference tool's exact mechanism.
        /// Null when the device has no such property (non-PCI or query failed).
        /// </summary>
        public static uint? GetSetupDiMaxMsiLimit(string fullInstanceId)
        {
            try
            {
                // PCI-only property; the class GUID scopes the enumeration.
                if (!fullInstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                    return null;
                string? classGuidText = null;
                using (var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{fullInstanceId}"))
                    classGuidText = enumKey?.GetValue("ClassGUID") as string;
                if (!Guid.TryParse(classGuidText?.Trim('{', '}', ' ', '\t'), out Guid classGuid))
                    return null;

                IntPtr set = NativeMethods.SetupApi.SetupDiGetClassDevs(ref classGuid, "PCI", IntPtr.Zero, NativeMethods.SetupApi.DIGCF_PRESENT);
                if (set == NativeMethods.SetupApi.INVALID_HANDLE_VALUE) return null;
                try
                {
                    uint index = 0;
                    while (true)
                    {
                        var data = new NativeMethods.SetupApi.SP_DEVINFO_DATA
                        {
                            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SetupApi.SP_DEVINFO_DATA>()
                        };
                        if (!NativeMethods.SetupApi.SetupDiEnumDeviceInfo(set, index++, ref data)) break;
                        var sb = new StringBuilder(512);
                        if (!NativeMethods.SetupApi.SetupDiGetDeviceInstanceId(set, ref data, sb, 512, out _) || sb.Length == 0)
                            continue;
                        if (!sb.ToString().Equals(fullInstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                        var key = NativeMethods.SetupApi.DEVPKEY_PciDevice_InterruptMessageMaximum;
                        byte[] buffer = new byte[4];
                        if (!NativeMethods.SetupApi.SetupDiGetDeviceProperty(set, ref data, ref key, out _, buffer, 4, out uint required, 0))
                            return null;
                        if (required < 4) return null;
                        uint value = BitConverter.ToUInt32(buffer, 0);
                        return value == 0 ? null : value;
                    }
                }
                finally
                {
                    NativeMethods.SetupApi.SetupDiDestroyDeviceInfoList(set);
                }
            }
            catch
            {
            }
            return null;
        }

        private static readonly object _maxCacheLock = new();
        private static Dictionary<string, uint>? _maxCache;

        private static string MaxCachePath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "kaliteConfig", "msi-max-cache.json");
                }
                catch { return string.Empty; }
            }
        }

        private static Dictionary<string, uint> LoadMaxCache()
        {
            lock (_maxCacheLock)
            {
                if (_maxCache is not null) return _maxCache;
                _maxCache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = MaxCachePath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetUInt32(out uint v) && v > 0)
                                _maxCache[prop.Name] = v;
                        }
                    }
                }
                catch { }
                return _maxCache;
            }
        }

        private static uint? GetCachedMaxMsiLimit(string fullInstanceId)
        {
            try
            {
                var cache = LoadMaxCache();
                lock (_maxCacheLock)
                    return cache.TryGetValue(fullInstanceId, out uint v) && v > 0 ? v : null;
            }
            catch { return null; }
        }

        private static void StoreMaxMsiCache(string fullInstanceId, uint value)
        {
            try
            {
                var cache = LoadMaxCache();
                lock (_maxCacheLock)
                {
                    if (cache.TryGetValue(fullInstanceId, out uint existing) && existing == value) return;
                    cache[fullInstanceId] = value;
                }
                string path = MaxCachePath;
                if (string.IsNullOrEmpty(path)) return;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json;
                lock (_maxCacheLock)
                    json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { /* cache is best-effort (e.g. non-elevated runs can't write ProgramData) */ }
        }

        public static string DevicePolicyName(int? v) => (v ?? 0) switch
        {
            0 => "IrqPolicyMachineDefault",
            1 => "IrqPolicyAllCloseProcessors",
            2 => "IrqPolicyOneCloseProcessor",
            3 => "IrqPolicyAllProcessorsInMachine",
            4 => "IrqPolicySpecifiedProcessors",
            5 => "IrqPolicySpreadMessagesAcrossAllProcessors",
            _ => "-"
        };

        public static string DevicePolicyShort(int? v) => (v ?? 0) switch
        {
            0 => "Default",
            1 => "All Close Proc",
            2 => "One Close Proc",
            3 => "All Proc in Machine",
            4 => "Specified Proc",
            5 => "Spread Messages Across All Proc",
            _ => "-"
        };

        public static string DevicePriorityName(int? v) => v switch
        {
            0 => "Undefined",
            1 => "Low",
            2 => "Normal",
            3 => "High",
            _ => "Undefined"
        };

        /// <summary>"2, 3" style list of assigned processors, like the reference
        /// tool (blank when nothing is explicitly assigned).</summary>
        public static string AffinityMaskText(ulong? mask)
        {
            if (mask is null || mask == 0) return string.Empty;
            var bits = new List<int>();
            ulong m = mask.Value;
            for (int i = 0; i < 64; i++)
                if ((m & (1UL << i)) != 0) bits.Add(i);
            return string.Join(", ", bits);
        }

        // DeviceDesc is often "@oemNN.inf,%token%;Friendly Name" - keep the friendly tail.
        private static string FriendlyName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            int semi = raw.LastIndexOf(';');
            string candidate = semi >= 0 && semi < raw.Length - 1 ? raw[(semi + 1)..].Trim() : raw.Trim();
            return string.IsNullOrWhiteSpace(candidate) ? raw.Trim() : candidate;
        }

        // ── Registry Write Methods (tuning pass) ────────────────────────────

        /// <summary>Enables or disables MSI mode. Returns true if the write succeeded.</summary>
        public bool SetMsiEnabled(string deviceId, bool enabled)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                int targetVal = enabled ? 1 : 0;
                Debug.WriteLine($"[AffinityService] Writing MSISupported={targetVal} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MSISupported", targetVal, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MSISupported");
                Debug.WriteLine($"[AffinityService] Read-back MSISupported={readback}");

                if (readback is null || (int)readback != targetVal)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMsiEnabled failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Deletes a value under MessageSignaledInterruptProperties. The
        /// reference tool deletes MessageNumberLimit to mean "Auto" (0).
        /// Returns true on success.
        /// </summary>
        public bool ClearMsiValue(string deviceId, string valueName)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key is null) return true;
                key.DeleteValue(valueName, throwOnMissingValue: false);
                return key.GetValue(valueName) is null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearMsiValue({valueName}) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets the MSI Limit. Returns true if the write succeeded.</summary>
        public bool SetMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                Debug.WriteLine($"[AffinityService] Writing MessageNumberLimit={limit} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MessageNumberLimit");
                Debug.WriteLine($"[AffinityService] Read-back MessageNumberLimit={readback}");

                if (readback is null || (int)readback != limit)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMsiLimit failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>Sets the Max MSI Limit. Returns true if the write succeeded.</summary>
        public bool SetMaxMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                Debug.WriteLine($"[AffinityService] Writing MaxMessageNumberLimit={limit} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MaxMessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MaxMessageNumberLimit");
                Debug.WriteLine($"[AffinityService] Read-back MaxMessageNumberLimit={readback}");

                if (readback is null || (int)readback != limit)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetMaxMsiLimit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets DevicePolicy (0-5). Returns true if the write succeeded.</summary>
        public bool SetDevicePolicy(string deviceId, int policy)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing DevicePolicy={policy} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePolicy", policy, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePolicy");
                Debug.WriteLine($"[AffinityService] Read-back DevicePolicy={readback}");

                if (readback is null || (int)readback != policy)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetDevicePolicy failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets DevicePriority (0=Low, 1=Normal, 2=High). Returns true if the write succeeded.</summary>
        public bool SetDevicePriority(string deviceId, int priority)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing DevicePriority={priority} to {path}");

                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePriority", priority, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePriority");
                Debug.WriteLine($"[AffinityService] Read-back DevicePriority={readback}");

                if (readback is null || (int)readback != priority)
                {
                    Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetDevicePriority failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets the AssignmentSetOverride bitmask (affinity mask). Pass 0 to clear
        /// (remove the value and let the OS decide). Returns true if the write succeeded.
        /// </summary>
        public bool SetAffinityMask(string deviceId, ulong mask)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Writing AssignmentSetOverride={mask:X} to {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                if (mask == 0)
                {
                    key.DeleteValue("AssignmentSetOverride", false);
                    object? readback = key.GetValue("AssignmentSetOverride");
                    if (readback is not null)
                    {
                        Debug.WriteLine("[AffinityService] Read-back deletion mismatched. Value still exists.");
                        return false;
                    }
                }
                else
                {
                    byte[] bytes = BitConverter.GetBytes(mask);
                    key.SetValue("AssignmentSetOverride", bytes, Microsoft.Win32.RegistryValueKind.Binary);
                    
                    object? readback = key.GetValue("AssignmentSetOverride");
                    if (readback is byte[] actualBytes)
                    {
                        ulong readMask = actualBytes.Length == 8 ? BitConverter.ToUInt64(actualBytes) : (ulong)BitConverter.ToUInt32(actualBytes);
                        Debug.WriteLine($"[AffinityService] Read-back AssignmentSetOverride={readMask:X}");
                        if (readMask != mask)
                        {
                            Debug.WriteLine("[AffinityService] Read-back failed or mismatched. Write failed.");
                            return false;
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[AffinityService] Read-back failed or mismatched type. Write failed.");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAffinityMask failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the affinity-related registry values, effectively restoring the
        /// device to OS defaults for that property. Returns true on success.
        /// </summary>
        public bool ClearAffinityPolicy(string deviceId, string valueName)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                Debug.WriteLine($"[AffinityService] Deleting {valueName} from {path}");
                
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    object? readback = key.GetValue(valueName);
                    if (readback is not null)
                    {
                        Debug.WriteLine("[AffinityService] Read-back deletion mismatched. Value still exists.");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearAffinityPolicy({valueName}) failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Programs the NIC RSS keys (*RSS, *RssBaseProcNumber, *MaxProcessors)
        /// for wired NDIS adapters, mirroring the reference tool. A zero mask
        /// removes the keys. Returns true on success (or when there is nothing
        /// to do for non-wired / NetAdapterCx adapters).
        /// </summary>
        public bool SetRSS(string deviceInstanceId, ulong mask)
        {
            try
            {
                string fullId = ResolveFullInstanceId(deviceInstanceId) ?? deviceInstanceId;
                using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{fullId}");
                string? driver = enumKey?.GetValue("Driver") as string;
                if (string.IsNullOrWhiteSpace(driver)) return false;
                using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}", writable: true);
                if (classKey is null) return false;
                // Wired LAN only, like the reference (*PhysicalMediaType 14).
                if (!string.Equals(classKey.GetValue("*PhysicalMediaType")?.ToString(), "14", StringComparison.Ordinal)) return true;
                if (IsNetAdapterCx(classKey)) return true;

                if (mask == 0)
                {
                    foreach (var v in new[] { "*RssBaseProcGroup", "*RssBaseProcNumber", "*MaxProcessors" })
                        classKey.DeleteValue(v, throwOnMissingValue: false);
                    return true;
                }

                var threads = new List<int>();
                for (int i = 0; i < 64; i++)
                    if ((mask & (1UL << i)) != 0) threads.Add(i);
                if (threads.Count == 0) return false;

                classKey.SetValue("*RSS", "1", Microsoft.Win32.RegistryValueKind.String);
                classKey.SetValue("*RssBaseProcGroup", "0", Microsoft.Win32.RegistryValueKind.String);
                classKey.SetValue("*RssBaseProcNumber", threads.Min().ToString(), Microsoft.Win32.RegistryValueKind.String);
                classKey.SetValue("*MaxProcessors", threads.Count.ToString(), Microsoft.Win32.RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetRSS failed: {ex.Message}");
                return false;
            }
        }

        // True for NetAdapterCx drivers (reference tool only programs RSS for
        // legacy NDIS). Unknowns default to legacy behavior, like the reference.
        private static bool IsNetAdapterCx(Microsoft.Win32.RegistryKey classKey)
        {
            try
            {
                using var ndi = classKey.OpenSubKey("Ndi");
                string? service = ndi?.GetValue("Service")?.ToString()?.TrimEnd('.');
                if (string.IsNullOrEmpty(service)) return false;
                using var svc = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
                if (svc?.GetValue("ImagePath") is not string imagePath) return false;
                string resolved = Environment.ExpandEnvironmentVariables(imagePath.StartsWith(@"\??\") ? imagePath[4..] : imagePath);
                string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                if (resolved.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase))
                    resolved = root + resolved[11..];
                if (!File.Exists(resolved)) return false;
                return Encoding.ASCII.GetString(File.ReadAllBytes(resolved)).Contains("NetAdapter", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Full registry instance path for an enumerated device. IDs stored by
        /// enumeration already carry the bus prefix ("PCI\VEN_xxxx…\instance");
        /// legacy unprefixed IDs are resolved by probing the known buses.
        /// Returns null when no bus holds the device any more.
        /// </summary>
        internal static string? ResolveFullInstanceId(string deviceInstanceId)
        {
            if (string.IsNullOrWhiteSpace(deviceInstanceId)) return null;
            foreach (var prefix in new[] { "PCI", "HDAUDIO", "USB" })
            {
                if (deviceInstanceId.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                    return deviceInstanceId;
            }
            foreach (var prefix in new[] { "PCI", "HDAUDIO", "USB" })
            {
                string testPath = $@"SYSTEM\CurrentControlSet\Enum\{prefix}\{deviceInstanceId}";
                using var testKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(testPath);
                if (testKey is not null) return $"{prefix}\\{deviceInstanceId}";
            }
            return null;
        }

        /// <summary>
        /// True while the device is still enumerated. Restarting a GPU (or
        /// installing a driver) re-enumerates the adapter and can hand it a new
        /// instance suffix, which left the Affinity page holding rows whose
        /// interrupt keys no longer existed - reads came back empty and writes
        /// silently landed nowhere.
        /// </summary>
        public bool DeviceExists(string deviceInstanceId)
            => ResolveFullInstanceId(deviceInstanceId) is not null;

        /// <summary>
        /// Restarts a device via pnputil so the updated interrupt settings take effect.
        /// </summary>
        public static async Task RestartDeviceAsync(string deviceInstanceId)
        {
            try
            {
                // pnputil needs the full bus-prefixed ID ("PCI\VEN_xxxx...\instance").
                string fullId = ResolveFullInstanceId(deviceInstanceId) ?? deviceInstanceId;

                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/restart-device \"{fullId}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"pnputil restart timed out for {fullId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestartDevice failed: {ex.Message}");
            }
        }
    }
}
