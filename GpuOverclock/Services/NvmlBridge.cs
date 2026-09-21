using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Minimal dynamic bridge to nvml.dll — the same driver-shipped library
    /// nvidia-smi itself links against — used only for fields classic NVAPI
    /// does not expose: absolute power draw in milliwatts and the absolute
    /// power limit. Loaded lazily with LoadLibrary; if the library or any
    /// entry point is missing, every read returns false and the module simply
    /// hides those fields (never surfaces zeros as real values).
    ///
    /// Note: binds to device index 0. On multi-GPU rigs this is the primary
    /// adapter in NVML ordering, which matches the typical single-GPU target
    /// for this module; the %-of-limit telemetry from NVAPI stays authoritative.
    /// </summary>
    internal static unsafe class NvmlBridge
    {
        private static bool _attempted;
        private static IntPtr _lib;
        private delegate int InitDelegate();
        private delegate int DeviceGetHandleDelegate(uint index, out IntPtr device);
        private delegate int GetPowerUsageDelegate(IntPtr device, out uint milliwatts);
        private delegate int GetPowerManagementLimitDelegate(IntPtr device, out uint milliwatts);
        private delegate int GetPcieThroughputDelegate(IntPtr device, int counter, out uint kilobytes);

        private static InitDelegate? _init;
        private static DeviceGetHandleDelegate? _getHandle;
        private static GetPowerUsageDelegate? _getPowerUsage;
        private static GetPcieThroughputDelegate? _getPcieThroughput;

        private static IntPtr _device;
        private static bool _deviceReady;

        /// <summary>
        /// Forgets the cached NVML device handle so the next read re-opens
        /// it. Call after any GPU device restart (pnputil/disable-enable):
        /// the cached handle may dangle afterwards, and a native call
        /// through it can fault where no managed catch can help (see
        /// HardwareQuiesceGate).
        /// </summary>
        public static void Reset()
        {
            _device = IntPtr.Zero;
            _deviceReady = false;
        }

        private static bool TryLoad()
        {
            if (_attempted) return _lib != IntPtr.Zero;
            _attempted = true;

            // Modern drivers (5xx+) place nvml.dll in System32; older installs
            // only had it under Program Files\NVIDIA Corporation\NVSMI.
            foreach (var path in new[] { "nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll" })
            {
                _lib = NativeLibraryShim.Load(path);
                if (_lib != IntPtr.Zero) break;
            }
            if (_lib == IntPtr.Zero) return false;

            try
            {
                _init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(
                    NativeLibraryShim.GetExport(_lib, "nvmlInit_v2"));
                _getHandle = Marshal.GetDelegateForFunctionPointer<DeviceGetHandleDelegate>(
                    NativeLibraryShim.GetExport(_lib, "nvmlDeviceGetHandleByIndex_v2"));
                _getPowerUsage = Marshal.GetDelegateForFunctionPointer<GetPowerUsageDelegate>(
                    NativeLibraryShim.GetExport(_lib, "nvmlDeviceGetPowerUsage"));
                return _init!() == 0 && _getHandle!(0, out _device) == 0;
            }
            catch
            {
                _lib = IntPtr.Zero;
                return false;
            }
        }

        private static bool EnsureDevice()
        {
            if (!TryLoad()) return false;
            if (!_deviceReady)
            {
                if (_getHandle!(0, out _device) != 0) return false;
                _deviceReady = true;
            }
            return true;
        }

        /// <summary>Current board power draw in milliwatts.</summary>
        public static bool TryReadPowerDrawMw(out uint milliwatts)
        {
            milliwatts = 0;
            if (!EnsureDevice()) return false;
            return _getPowerUsage!(_device, out milliwatts) == 0 && milliwatts > 0;
        }

        /// <summary>
        /// Cumulative PCIe transfer counters in KB (counter 0 = host-to-device,
        /// 1 = device-to-host). Callers difference across ticks for a rate.
        /// Resolved optionally AFTER the core load succeeds, so a missing
        /// export can never disable the power reads that share this bridge.
        /// </summary>
        public static bool TryReadPcieThroughputKb(int counter, out uint kilobytes)
        {
            kilobytes = 0;
            if (!EnsureDevice()) return false;
            if (_getPcieThroughput is null)
            {
                var p = NativeLibraryShim.GetExportOptional(_lib, "nvmlDeviceGetPcieThroughput");
                if (p == IntPtr.Zero) return false;
                try
                {
                    _getPcieThroughput = Marshal.GetDelegateForFunctionPointer<GetPcieThroughputDelegate>(p);
                }
                catch { return false; }
            }
            return _getPcieThroughput(_device, counter, out kilobytes) == 0;
        }

        /// <summary>Default (factory) power limit in milliwatts, when the driver reports it.</summary>
        public static bool TryReadDefaultPowerLimitMw(out uint milliwatts)
        {
            milliwatts = 0;
            // Constraints give min/max; the default limit sits at the driver's
            // factory target. nvmlDeviceGetPowerManagementDefaultLimit is the
            // direct source when present.
            if (!EnsureDevice()) return false;
            var p = NativeLibraryShim.GetExportOptional(_lib, "nvmlDeviceGetPowerManagementDefaultLimit");
            if (p == IntPtr.Zero) return false;
            var d = Marshal.GetDelegateForFunctionPointer<GetPowerManagementLimitDelegate>(p);
            return d(_device, out milliwatts) == 0 && milliwatts > 0;
        }
    }

    /// <summary>Kernel32 loader helpers (avoids pulling in a P/Invoke source generator surface in several files).</summary>
    internal static class NativeLibraryShim
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static IntPtr Load(string path) => LoadLibraryW(path);
        public static IntPtr GetExport(IntPtr module, string name) => GetProcAddress(module, name);
        public static IntPtr GetExportOptional(IntPtr module, string name) => GetProcAddress(module, name);
    }
}
