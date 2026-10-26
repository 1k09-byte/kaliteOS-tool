using System;

namespace kaliteConfig.GpuOverclock.Services
{
        internal static class DisplayDriverVersion
    {
        private static readonly object Gate = new();
        private static bool _read;
        private static string? _amd;

                public static string? AmdDisplayDriver
        {
            get
            {
                lock (Gate)
                {
                    if (_read) return _amd;
                    _read = true;
                    try
                    {
                        var scope = new System.Management.ManagementScope(@"\\.\root\cimv2");
                        var query = new System.Management.ObjectQuery(
                            "SELECT DriverVersion, ProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass='DISPLAY'");
                        var options = new System.Management.EnumerationOptions
                        {
                            Timeout = TimeSpan.FromSeconds(5),
                            ReturnImmediately = true,
                        };
                        using var searcher = new System.Management.ManagementObjectSearcher(scope, query, options);
                        foreach (System.Management.ManagementObject o in searcher.Get())
                        {
                            using (o)
                            {
                                var provider = o["ProviderName"] as string ?? string.Empty;
                                var version = o["DriverVersion"] as string;
                                if (!string.IsNullOrWhiteSpace(version) &&
                                    (provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                     provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)))
                                {
                                    _amd = version;
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {

                    }
                    return _amd;
                }
            }
        }
    }
}
