using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.GpuOverclock.Services
{
    internal readonly record struct AmdGpuMetrics(
        int? CoreClockMHz,
        int? MemClockMHz,
        int? GpuTempC,
        int? HotspotTempC,
        int? FanRpm,
        int? FanPercent,
        int? GpuUsagePercent,
        int? VramUsedMb,
        int? VoltageMv,
        double? PowerW,
        double? BoardPowerW);

    internal static partial class AmdAdlxInterop
    {
        private const int SystemGetPerformanceMonitoringServices = 9;

        private const int PerfGetCurrentGpuMetrics = 18;
        private const int PerfGetSupportedGpuMetrics = 21;

        private const int SupportGetFanSpeedRange = 19;

        private const int MetricsGpuUsage = 4;
        private const int MetricsCoreClock = 5;
        private const int MetricsMemClock = 6;
        private const int MetricsGpuTemp = 7;
        private const int MetricsHotspotTemp = 8;
        private const int MetricsGpuPower = 9;
        private const int MetricsBoardPower = 10;
        private const int MetricsFanSpeed = 11;
        private const int MetricsVramUsed = 12;
        private const int MetricsVoltage = 13;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPerformanceServicesFn(IntPtr self, out IntPtr ppServices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCurrentGpuMetricsFn(IntPtr self, IntPtr gpu, out IntPtr ppMetrics);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSupportedGpuMetricsFn(IntPtr self, IntPtr gpu, out IntPtr ppSupport);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MetricsRangeFn(IntPtr self, out int minimum, out int maximum);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MetricsIntFn(IntPtr self, out int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MetricsDoubleFn(IntPtr self, out double value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long MetricsReleaseFn(IntPtr self);

        private static IntPtr _performanceServices;
        private static int _fanMaxRpm;

        private static IntPtr PerformanceServices
        {
            get
            {
                if (!EnsureInitialized()) return IntPtr.Zero;
                if (_performanceServices != IntPtr.Zero) return _performanceServices;

                try
                {
                    if (Fn<GetPerformanceServicesFn>(_system, SystemGetPerformanceMonitoringServices)(
                            _system, out IntPtr services) == 0 && services != IntPtr.Zero)
                    {
                        _performanceServices = services;
                    }
                }
                catch (Exception)
                {
                    _performanceServices = IntPtr.Zero;
                }

                return _performanceServices;
            }
        }

        internal static bool TryReadGpuMetrics(IntPtr gpu, out AmdGpuMetrics metrics)
        {
            metrics = default;
            if (gpu == IntPtr.Zero) return false;

            lock (Gate)
            {
                IntPtr services = PerformanceServices;
                if (services == IntPtr.Zero) return false;

                IntPtr raw = IntPtr.Zero;
                try
                {
                    if (Fn<GetCurrentGpuMetricsFn>(services, PerfGetCurrentGpuMetrics)(
                            services, gpu, out raw) != 0 || raw == IntPtr.Zero)
                    {
                        return false;
                    }

                    int? fanRpm = ReadInt(raw, MetricsFanSpeed);
                    int? fanPercent = null;
                    if (fanRpm is { } rpm)
                    {
                        int maxRpm = FanMaxRpm(services, gpu);
                        if (maxRpm > 0)
                            fanPercent = Math.Clamp((int)Math.Round(rpm * 100.0 / maxRpm), 0, 100);
                    }

                    metrics = new AmdGpuMetrics(
                        CoreClockMHz: ReadInt(raw, MetricsCoreClock),
                        MemClockMHz: ReadInt(raw, MetricsMemClock),
                        GpuTempC: ReadRoundedInt(raw, MetricsGpuTemp),
                        HotspotTempC: ReadRoundedInt(raw, MetricsHotspotTemp),
                        FanRpm: fanRpm,
                        FanPercent: fanPercent,
                        GpuUsagePercent: ReadRoundedInt(raw, MetricsGpuUsage),
                        VramUsedMb: ReadInt(raw, MetricsVramUsed),
                        VoltageMv: ReadInt(raw, MetricsVoltage),
                        PowerW: ReadDouble(raw, MetricsGpuPower),
                        BoardPowerW: ReadDouble(raw, MetricsBoardPower));
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    if (raw != IntPtr.Zero) MetricsRelease(raw);
                }
            }
        }

        private static int FanMaxRpm(IntPtr services, IntPtr gpu)
        {
            if (_fanMaxRpm > 0) return _fanMaxRpm;

            IntPtr support = IntPtr.Zero;
            try
            {
                if (Fn<GetSupportedGpuMetricsFn>(services, PerfGetSupportedGpuMetrics)(
                        services, gpu, out support) != 0 || support == IntPtr.Zero)
                    return 0;

                if (Fn<MetricsRangeFn>(support, SupportGetFanSpeedRange)(
                        support, out _, out int max) == 0 && max > 0)
                {
                    _fanMaxRpm = max;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (support != IntPtr.Zero) MetricsRelease(support);
            }

            return _fanMaxRpm;
        }

        private static int? ReadInt(IntPtr metrics, int slot)
        {
            try
            {
                return Fn<MetricsIntFn>(metrics, slot)(metrics, out int value) == 0 ? value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int? ReadRoundedInt(IntPtr metrics, int slot)
        {
            try
            {
                return Fn<MetricsDoubleFn>(metrics, slot)(metrics, out double value) == 0
                    ? (int)Math.Round(value)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? ReadDouble(IntPtr metrics, int slot)
        {
            try
            {
                return Fn<MetricsDoubleFn>(metrics, slot)(metrics, out double value) == 0 ? value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void MetricsRelease(IntPtr iface)
        {
            try { Fn<MetricsReleaseFn>(iface, 1)(iface); }
            catch (Exception) { }
        }

        private static void ReleasePerformanceServices()
        {
            if (_performanceServices == IntPtr.Zero) return;
            MetricsRelease(_performanceServices);
            _performanceServices = IntPtr.Zero;
            _fanMaxRpm = 0;
        }
    }
}
