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
    public class AffinityService
    {
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

                                if (key.GetValue("Phantom") is int phantom && phantom == 1) continue;

                                string busPrefix = root.Substring(root.LastIndexOf('\\') + 1);
                                string instanceId = busPrefix + "\\" + device + "\\" + instance;
                                if (!NativeMethods.CfgMgr32.IsDevicePresent(instanceId)) continue;

                                string classGuid = (key.GetValue("ClassGUID") as string ?? "").Trim();
                                if (string.IsNullOrEmpty(classGuid))
                                    classGuid = (deviceKey.GetValue("ClassGUID") as string ?? "").Trim();
                            string? category = MapClass(classGuid);
                            if (category is null) continue;
                            if (root.EndsWith(@"\USB", StringComparison.OrdinalIgnoreCase) &&
                                category != "Graphics" && category != "Network" && category != "Audio")
                                continue;

                                string rawName = (key.GetValue("DeviceDesc") as string ?? "").Trim();
                                string name = FriendlyName(rawName);
                                if (string.IsNullOrEmpty(name) || IsVirtualAdapter(name)) continue;
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
                catch (Exception)
                {
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
            catch (Exception)
            {
            }
            return new InterruptInfo(msi, limit, maxLimit, policy, priority, mask);
        }

        public static uint? GetSetupDiMaxMsiLimit(string fullInstanceId)
        {
            try
            {
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
            catch {  }
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

        public static string AffinityMaskText(ulong? mask)
        {
            if (mask is null || mask == 0) return string.Empty;
            var bits = new List<int>();
            ulong m = mask.Value;
            for (int i = 0; i < 64; i++)
                if ((m & (1UL << i)) != 0) bits.Add(i);
            return string.Join(", ", bits);
        }

        private static string FriendlyName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            int semi = raw.LastIndexOf(';');
            string candidate = semi >= 0 && semi < raw.Length - 1 ? raw[(semi + 1)..].Trim() : raw.Trim();
            return string.IsNullOrWhiteSpace(candidate) ? raw.Trim() : candidate;
        }

        public bool SetMsiEnabled(string deviceId, bool enabled)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                int targetVal = enabled ? 1 : 0;
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MSISupported", targetVal, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MSISupported");

                if (readback is null || (int)readback != targetVal)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        
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
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MessageNumberLimit");

                if (readback is null || (int)readback != limit)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        
        public bool SetMaxMsiLimit(string deviceId, int limit)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("MaxMessageNumberLimit", limit, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("MaxMessageNumberLimit");

                if (readback is null || (int)readback != limit)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetDevicePolicy(string deviceId, int policy)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePolicy", policy, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePolicy");

                if (readback is null || (int)readback != policy)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetDevicePriority(string deviceId, int priority)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";

                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                key.SetValue("DevicePriority", priority, Microsoft.Win32.RegistryValueKind.DWord);
                
                object? readback = key.GetValue("DevicePriority");

                if (readback is null || (int)readback != priority)
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetAffinityMask(string deviceId, ulong mask)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path);
                if (mask == 0)
                {
                    key.DeleteValue("AssignmentSetOverride", false);
                    object? readback = key.GetValue("AssignmentSetOverride");
                    if (readback is not null)
                    {
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
                        if (readMask != mask)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool ClearAffinityPolicy(string deviceId, string valueName)
        {
            try
            {
                string path = $@"SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\Affinity Policy";
                
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    object? readback = key.GetValue(valueName);
                    if (readback is not null)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public sealed record RssInfo(bool Supported, bool Enabled, int Queues, int BaseProcessor, int MaxProcessors, string Summary);

        public sealed record RssApplyResult(bool Changed, bool Supported, string Message);

        /// <summary>
        /// Reads the NDIS RSS configuration Windows exposes in the driver class key.
        /// Network packet steering goes through RSS, not through the IRQ affinity mask,
        /// so this is what actually decides which CPUs process received packets.
        /// </summary>
        public RssInfo GetRssInfo(string deviceInstanceId)
        {
            try
            {
                using var classKey = OpenDriverClassKey(deviceInstanceId, writable: false);
                if (classKey is null)
                    return new RssInfo(false, false, 0, 0, 0, "RSS settings are not readable for this device.");
                return ReadRssInfo(classKey);
            }
            catch
            {
                return new RssInfo(false, false, 0, 0, 0, "RSS settings are not readable for this device.");
            }
        }

        /// <summary>
        /// Applies a processor mask to NDIS RSS. RSS can only express a contiguous run
        /// starting at a base processor, so the mask is translated to base + count and
        /// clamped to the queue count the driver actually supports.
        /// </summary>
        public RssApplyResult ConfigureRssForAffinity(string deviceInstanceId, ulong mask)
        {
            try
            {
                using var classKey = OpenDriverClassKey(deviceInstanceId, writable: true);
                if (classKey is null)
                    return new RssApplyResult(false, false, "RSS settings could not be opened for this device.");

                if (!HasRssSupport(classKey))
                    return new RssApplyResult(false, false,
                        "This adapter's driver exposes no NDIS RSS settings; only the IRQ mask was applied.");

                var before = ReadRssInfo(classKey);

                if (mask == 0)
                {
                    bool cleared = false;
                    foreach (var value in new[] { "*RssBaseProcGroup", "*RssBaseProcNumber", "*MaxProcessors" })
                    {
                        if (classKey.GetValue(value) is null) continue;
                        classKey.DeleteValue(value, throwOnMissingValue: false);
                        cleared = true;
                    }
                    return new RssApplyResult(cleared, true,
                        cleared ? "RSS override cleared - Windows picks the processors again." : before.Summary);
                }

                var threads = new List<int>();
                int addressable = Math.Min(64, Environment.ProcessorCount);
                for (int i = 0; i < addressable; i++)
                    if ((mask & (1UL << i)) != 0) threads.Add(i);
                if (threads.Count == 0)
                    return new RssApplyResult(false, true,
                        "RSS: the selected processors are outside processor group 0, so no RSS mask was written.");

                int queues = before.Queues > 0 ? before.Queues : addressable;
                int maxProcessors = Math.Clamp(threads.Count, 1, Math.Min(queues, addressable));
                int baseProcessor = threads.Min();

                bool changed = !before.Enabled
                    || before.BaseProcessor != baseProcessor
                    || before.MaxProcessors != maxProcessors;

                if (changed)
                {
                    classKey.SetValue("*RSS", "1", Microsoft.Win32.RegistryValueKind.String);
                    classKey.SetValue("*RssBaseProcGroup", "0", Microsoft.Win32.RegistryValueKind.String);
                    classKey.SetValue("*RssBaseProcNumber", baseProcessor.ToString(), Microsoft.Win32.RegistryValueKind.String);
                    classKey.SetValue("*MaxProcessors", maxProcessors.ToString(), Microsoft.Win32.RegistryValueKind.String);
                }

                return new RssApplyResult(changed, true,
                    $"RSS on - queues start at CPU {baseProcessor}, up to {maxProcessors} processor(s) across {queues} queue(s).");
            }
            catch (Exception ex)
            {
                return new RssApplyResult(false, true, $"RSS could not be written: {ex.Message}");
            }
        }

        public bool ClearRssOverride(string deviceInstanceId)
        {
            try
            {
                using var classKey = OpenDriverClassKey(deviceInstanceId, writable: true);
                if (classKey is null) return false;
                foreach (var value in new[] { "*RssBaseProcGroup", "*RssBaseProcNumber", "*MaxProcessors" })
                    classKey.DeleteValue(value, throwOnMissingValue: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Microsoft.Win32.RegistryKey? OpenDriverClassKey(string deviceInstanceId, bool writable)
        {
            string fullId = ResolveFullInstanceId(deviceInstanceId) ?? deviceInstanceId;
            using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{fullId}");
            string? driver = enumKey?.GetValue("Driver") as string;
            if (string.IsNullOrWhiteSpace(driver)) return null;
            return Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{driver}", writable);
        }

        private static bool HasRssSupport(Microsoft.Win32.RegistryKey classKey)
        {
            if (classKey.GetValue("*NumRssQueues") is not null) return true;
            if (classKey.GetValue("*RSS") is not null) return true;
            if (classKey.GetValue("*RssBaseProcNumber") is not null) return true;
            using var rss = classKey.OpenSubKey(@"Ndi\Params\*RSS");
            using var queues = classKey.OpenSubKey(@"Ndi\Params\*NumRssQueues");
            using var baseProcessor = classKey.OpenSubKey(@"Ndi\Params\*RssBaseProcNumber");
            return rss is not null || queues is not null || baseProcessor is not null;
        }

        private static RssInfo ReadRssInfo(Microsoft.Win32.RegistryKey classKey)
        {
            if (!HasRssSupport(classKey))
                return new RssInfo(false, false, 0, 0, 0,
                    "This adapter's driver exposes no NDIS RSS settings, so packet steering cannot be pinned.");

            int queues = ReadIntValue(classKey, "*NumRssQueues")
                ?? ReadParamDefault(classKey, "*NumRssQueues")
                ?? Math.Min(Environment.ProcessorCount, 16);
            if (queues <= 0) queues = 1;

            bool enabled = !string.Equals(
                classKey.GetValue("*RSS")?.ToString(), "0", StringComparison.Ordinal);
            int baseProcessor = ReadIntValue(classKey, "*RssBaseProcNumber")
                ?? ReadParamDefault(classKey, "*RssBaseProcNumber") ?? 0;
            int maxProcessors = ReadIntValue(classKey, "*MaxProcessors") ?? queues;

            string summary = enabled
                ? $"RSS on - base CPU {baseProcessor}, up to {maxProcessors} processor(s) across {queues} queue(s)."
                : "RSS is disabled for this adapter.";
            return new RssInfo(true, enabled, queues, baseProcessor, maxProcessors, summary);
        }

        private static int? ReadIntValue(Microsoft.Win32.RegistryKey classKey, string name)
        {
            object? value = classKey.GetValue(name);
            if (value is null) return null;
            if (value is int i) return i;
            return int.TryParse(value.ToString(), out int parsed) ? parsed : null;
        }

        private static int? ReadParamDefault(Microsoft.Win32.RegistryKey classKey, string keyword)
        {
            using var param = classKey.OpenSubKey($@"Ndi\Params\{keyword}");
            string? text = param?.GetValue("Default")?.ToString();
            return int.TryParse(text, out int parsed) ? parsed : null;
        }

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

        public bool DeviceExists(string deviceInstanceId)
            => ResolveFullInstanceId(deviceInstanceId) is not null;

        public static async Task RestartDeviceAsync(string deviceInstanceId)
        {
            try
            {
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
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
