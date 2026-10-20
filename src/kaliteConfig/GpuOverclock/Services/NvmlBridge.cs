using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.GpuOverclock.Services
{
    internal static unsafe class NvmlBridge
    {
        private static bool _attempted;
        private static IntPtr _lib;
        private delegate int InitDelegate();
        private delegate int ShutdownDelegate();
        private delegate int DeviceGetHandleDelegate(uint index, out IntPtr device);
        private delegate int GetPowerUsageDelegate(IntPtr device, out uint milliwatts);
        private delegate int GetPowerManagementLimitDelegate(IntPtr device, out uint milliwatts);
        private delegate int GetPcieThroughputDelegate(IntPtr device, int counter, out uint kilobytes);

        private static InitDelegate? _init;
        private static ShutdownDelegate? _shutdown;
        private static DeviceGetHandleDelegate? _getHandle;
        private static GetPowerUsageDelegate? _getPowerUsage;
        private static GetPcieThroughputDelegate? _getPcieThroughput;

        private static IntPtr _device;
        private static bool _deviceReady;

        public static void Reset()
        {
            if (_lib != IntPtr.Zero && _shutdown != null)
            {
                try { _shutdown(); } catch { }
            }
            _device = IntPtr.Zero;
            _deviceReady = false;
            _attempted = false;
        }

        private static bool TryLoad()
        {
            if (_attempted) return _lib != IntPtr.Zero;
            _attempted = true;

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
                _shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownDelegate>(
                    NativeLibraryShim.GetExport(_lib, "nvmlShutdown"));
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

        public static bool TryReadPowerDrawMw(out uint milliwatts)
        {
            milliwatts = 0;
            if (!EnsureDevice()) return false;
            return _getPowerUsage!(_device, out milliwatts) == 0 && milliwatts > 0;
        }

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

        public static bool TryReadDefaultPowerLimitMw(out uint milliwatts)
        {
            milliwatts = 0;
            if (!EnsureDevice()) return false;
            var p = NativeLibraryShim.GetExportOptional(_lib, "nvmlDeviceGetPowerManagementDefaultLimit");
            if (p == IntPtr.Zero) return false;
            var d = Marshal.GetDelegateForFunctionPointer<GetPowerManagementLimitDelegate>(p);
            return d(_device, out milliwatts) == 0 && milliwatts > 0;
        }
    }

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
