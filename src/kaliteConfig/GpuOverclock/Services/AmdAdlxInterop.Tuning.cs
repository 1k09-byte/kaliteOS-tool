using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace kaliteConfig.GpuOverclock.Services
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlxIntRange
    {
        public int MinValue;
        public int MaxValue;
        public int Step;

        public override string ToString() => $"{MinValue}..{MaxValue} step {Step}";
    }

    internal sealed class AmdGpuTuningSession : IDisposable
    {
        private readonly IntPtr _gpu;
        private readonly IntPtr _tuning;
        private IntPtr _gfx2;
        private IntPtr _vram2;
        private IntPtr _fan;
        private IntPtr _fan1;
        private IntPtr _power;
        private bool _disposed;

        internal AmdGpuTuningSession(IntPtr gpu, IntPtr tuningServices, AmdGpuTuningSupport support)
        {
            _gpu = gpu;
            _tuning = tuningServices;
            Support = support;
        }

        internal AmdGpuTuningSupport Support { get; }

        internal bool IsUsable => !_disposed && _gpu != IntPtr.Zero;

        internal IntPtr GpuHandle => _gpu;

        private const int TuningGetManualGfx = 14;
        private const int TuningGetManualVram = 15;
        private const int TuningGetManualFan = 16;
        private const int TuningGetManualPower = 17;

        private const int IfaceRelease = 1;
        private const int IfaceQueryInterface = 2;

        private const int Gfx2GetMaxFrequencyRange = 6;
        private const int Gfx2GetMaxFrequency = 7;
        private const int Gfx2SetMaxFrequency = 8;

        private const int Vram2GetMaxFrequencyRange = 7;
        private const int Vram2GetMaxFrequency = 8;
        private const int Vram2SetMaxFrequency = 9;

        private const int PowerGetLimitRange = 3;
        private const int PowerGetLimit = 4;
        private const int PowerSetLimit = 5;

        private const int FanGetTuningRanges = 3;
        private const int FanGetTuningStates = 4;
        private const int FanSetTuningStates = 7;
        private const int FanIsSupportedZeroRpm = 8;
        private const int FanGetZeroRpmState = 9;
        private const int FanSetZeroRpmState = 10;

        private const int FanGetDefaultTuningStates = 23;
        private const int FanGetDefaultZeroRpm = 27;

        private const int FanStateGetSpeed = 3;
        private const int FanStateSetSpeed = 4;
        private const int FanStateGetTemperature = 5;
        private const int FanStateSetTemperature = 6;
        private const int FanStateListSize = 3;
        private const int FanStateListAt = 11;

        private const string IidGraphicsTuning2 = "IADLXManualGraphicsTuning2";
        private const string IidVramTuning2 = "IADLXManualVRAMTuning2";
        private const string IidFanTuning = "IADLXManualFanTuning";
        private const string IidFanTuning1 = "IADLXManualFanTuning1";
        private const string IidPowerTuning = "IADLXManualPowerTuning";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, IntPtr interfaceId, out IntPtr ppInterface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDomainFn(IntPtr self, IntPtr gpu, out IntPtr ppDomain);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntRangeFn(IntPtr self, out AdlxIntRange range);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntRange2Fn(IntPtr self, out AdlxIntRange first, out AdlxIntRange second);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntFn(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetIntFn(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBoolFn(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBoolFn(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObjectFn(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetObjectFn(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ListAtStateFn(IntPtr self, uint location, out IntPtr ppState);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ListCountFn(IntPtr self);

        private IntPtr Domain(int servicesSlot, string iid)
        {
            if (!IsUsable || _tuning == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetDomainFn>(_tuning, servicesSlot)(_tuning, _gpu, out IntPtr domain) != 0
                    || domain == IntPtr.Zero)
                    return IntPtr.Zero;

                IntPtr iidPtr = Marshal.StringToHGlobalUni(iid);
                try
                {
                    if (AmdAdlxInterop.Fn<QueryInterfaceFn>(domain, IfaceQueryInterface)(domain, iidPtr, out IntPtr versioned) != 0
                        || versioned == IntPtr.Zero)
                    {
                        AmdAdlxInterop.Fn<ReleaseFn>(domain, IfaceRelease)(domain);
                        return IntPtr.Zero;
                    }

                    AmdAdlxInterop.Fn<ReleaseFn>(domain, IfaceRelease)(domain);
                    return versioned;
                }
                finally
                {
                    Marshal.FreeHGlobal(iidPtr);
                }
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private IntPtr Gfx2 => _gfx2 != IntPtr.Zero ? _gfx2 : (_gfx2 = Domain(TuningGetManualGfx, IidGraphicsTuning2));
        private IntPtr Vram2 => _vram2 != IntPtr.Zero ? _vram2 : (_vram2 = Domain(TuningGetManualVram, IidVramTuning2));
        private IntPtr Fan => _fan != IntPtr.Zero ? _fan : (_fan = Domain(TuningGetManualFan, IidFanTuning));
        private IntPtr Fan1 => _fan1 != IntPtr.Zero ? _fan1 : (_fan1 = QueryInterface(Fan, IidFanTuning1));
        private IntPtr Power => _power != IntPtr.Zero ? _power : (_power = Domain(TuningGetManualPower, IidPowerTuning));

        private IntPtr QueryInterface(IntPtr iface, string iid)
        {
            if (iface == IntPtr.Zero) return IntPtr.Zero;
            IntPtr iidPtr = Marshal.StringToHGlobalUni(iid);
            try
            {
                return AmdAdlxInterop.Fn<QueryInterfaceFn>(iface, IfaceQueryInterface)(
                    iface, iidPtr, out IntPtr versioned) == 0
                    ? versioned
                    : IntPtr.Zero;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(iidPtr);
            }
        }

        internal AdlxIntRange? ReadCoreMaxFrequencyRange()
        {
            IntPtr gfx = Gfx2;
            if (gfx == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntRangeFn>(gfx, Gfx2GetMaxFrequencyRange)(gfx, out AdlxIntRange range) == 0
                    ? range : null;
            }
            catch (Exception) { return null; }
        }

        internal int? ReadCoreMaxFrequency()
        {
            IntPtr gfx = Gfx2;
            if (gfx == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntFn>(gfx, Gfx2GetMaxFrequency)(gfx, out int mhz) == 0 ? mhz : null;
            }
            catch (Exception) { return null; }
        }

        internal bool TrySetCoreMaxFrequency(int mhz) => TrySet(Gfx2, Gfx2SetMaxFrequency, mhz);

        internal AdlxIntRange? ReadMaxVramFrequencyRange()
        {
            IntPtr vram = Vram2;
            if (vram == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntRangeFn>(vram, Vram2GetMaxFrequencyRange)(vram, out AdlxIntRange range) == 0
                    ? range : null;
            }
            catch (Exception) { return null; }
        }

        internal int? ReadMaxVramFrequency()
        {
            IntPtr vram = Vram2;
            if (vram == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntFn>(vram, Vram2GetMaxFrequency)(vram, out int mhz) == 0 ? mhz : null;
            }
            catch (Exception) { return null; }
        }

        internal bool TrySetMaxVramFrequency(int mhz) => TrySet(Vram2, Vram2SetMaxFrequency, mhz);

        internal AdlxIntRange? ReadPowerLimitRange()
        {
            IntPtr power = Power;
            if (power == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntRangeFn>(power, PowerGetLimitRange)(power, out AdlxIntRange range) == 0
                    ? range : null;
            }
            catch (Exception) { return null; }
        }

        internal int? ReadPowerLimit()
        {
            IntPtr power = Power;
            if (power == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntFn>(power, PowerGetLimit)(power, out int percent) == 0 ? percent : null;
            }
            catch (Exception) { return null; }
        }

        internal bool TrySetPowerLimit(int percent) => TrySet(Power, PowerSetLimit, percent);

        internal AdlxIntRange? ReadFanSpeedRange()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetIntRange2Fn>(fan, FanGetTuningRanges)(fan, out AdlxIntRange speed, out _) == 0
                    ? speed : null;
            }
            catch (Exception) { return null; }
        }

        internal bool TrySetFanSpeed(int percent) => TrySetFanSpeeds(new[] { percent });

        internal bool TrySetFanSpeeds(IReadOnlyList<int> percents)
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero || percents.Count == 0) return false;

            IntPtr list = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan, FanGetTuningStates)(fan, out list) != 0 || list == IntPtr.Zero)
                    return false;

                uint count = AmdAdlxInterop.Fn<ListCountFn>(list, FanStateListSize)(list);
                if (count == 0) return false;

                for (uint i = 0; i < count; i++)
                {
                    if (AmdAdlxInterop.Fn<ListAtStateFn>(list, FanStateListAt)(list, i, out IntPtr state) != 0
                        || state == IntPtr.Zero)
                        return false;
                    try
                    {
                        int value = percents[Math.Min((int)i, percents.Count - 1)];
                        if (AmdAdlxInterop.Fn<SetIntFn>(state, FanStateSetSpeed)(state, value) != 0)
                            return false;
                    }
                    finally
                    {
                        SafeRelease(state);
                    }
                }

                return AmdAdlxInterop.Fn<SetObjectFn>(fan, FanSetTuningStates)(fan, list) == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        internal int[]? ReadFanSpeedStates()
        {
            var curve = ReadFanCurve();
            if (curve is null) return null;
            var speeds = new int[curve.Length];
            for (int i = 0; i < curve.Length; i++) speeds[i] = curve[i].SpeedPercent;
            return speeds;
        }

        internal (int TempC, int SpeedPercent)[]? ReadFanCurve()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;

            IntPtr list = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan, FanGetTuningStates)(fan, out list) != 0 || list == IntPtr.Zero)
                    return null;
                return ReadStateCurve(list);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        internal (int TempC, int SpeedPercent)[]? ReadDefaultFanCurve()
        {
            IntPtr fan1 = Fan1;
            if (fan1 == IntPtr.Zero) return null;

            IntPtr list = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan1, FanGetDefaultTuningStates)(fan1, out list) != 0
                    || list == IntPtr.Zero)
                    return null;
                return ReadStateCurve(list);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        internal bool? ReadDefaultZeroRpm()
        {
            IntPtr fan1 = Fan1;
            if (fan1 == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetBoolFn>(fan1, FanGetDefaultZeroRpm)(fan1, out byte on) == 0
                    ? on != 0
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static (int TempC, int SpeedPercent)[]? ReadStateCurve(IntPtr list)
        {
            uint count = AmdAdlxInterop.Fn<ListCountFn>(list, FanStateListSize)(list);
            if (count == 0) return null;

            var curve = new (int TempC, int SpeedPercent)[count];
            for (uint i = 0; i < count; i++)
            {
                if (AmdAdlxInterop.Fn<ListAtStateFn>(list, FanStateListAt)(list, i, out IntPtr state) != 0
                    || state == IntPtr.Zero)
                    return null;
                try
                {
                    if (AmdAdlxInterop.Fn<GetIntFn>(state, FanStateGetSpeed)(state, out int speed) != 0)
                        return null;
                    AmdAdlxInterop.Fn<GetIntFn>(state, FanStateGetTemperature)(state, out int temp);
                    curve[i] = (temp, speed);
                }
                finally
                {
                    SafeRelease(state);
                }
            }

            return curve;
        }

        internal bool TrySetFanCurve(IReadOnlyList<(int TempC, int SpeedPercent)> curve)
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero || curve.Count == 0) return false;

            IntPtr list = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan, FanGetTuningStates)(fan, out list) != 0 || list == IntPtr.Zero)
                    return false;

                uint count = AmdAdlxInterop.Fn<ListCountFn>(list, FanStateListSize)(list);
                if (count == 0) return false;

                for (uint i = 0; i < count; i++)
                {
                    if (AmdAdlxInterop.Fn<ListAtStateFn>(list, FanStateListAt)(list, i, out IntPtr state) != 0
                        || state == IntPtr.Zero)
                        return false;
                    try
                    {
                        var point = curve[Math.Min((int)i, curve.Count - 1)];
                        if (point.TempC > 0 &&
                            AmdAdlxInterop.Fn<SetIntFn>(state, FanStateSetTemperature)(state, point.TempC) != 0)
                            return false;
                        if (AmdAdlxInterop.Fn<SetIntFn>(state, FanStateSetSpeed)(state, point.SpeedPercent) != 0)
                            return false;
                    }
                    finally
                    {
                        SafeRelease(state);
                    }
                }

                return AmdAdlxInterop.Fn<SetObjectFn>(fan, FanSetTuningStates)(fan, list) == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        internal int? ReadFanSpeed()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;
            IntPtr list = IntPtr.Zero, state = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan, FanGetTuningStates)(fan, out list) != 0 || list == IntPtr.Zero)
                    return null;
                if (AmdAdlxInterop.Fn<ListCountFn>(list, FanStateListSize)(list) == 0) return null;
                if (AmdAdlxInterop.Fn<ListAtStateFn>(list, FanStateListAt)(list, 0, out state) != 0 || state == IntPtr.Zero)
                    return null;

                return AmdAdlxInterop.Fn<GetIntFn>(state, FanStateGetSpeed)(state, out int percent) == 0 ? percent : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (state != IntPtr.Zero) SafeRelease(state);
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        internal bool? IsZeroRpmSupported()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetBoolFn>(fan, FanIsSupportedZeroRpm)(fan, out byte ok) == 0 ? ok != 0 : null;
            }
            catch (Exception) { return null; }
        }

        internal bool? ReadZeroRpm()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetBoolFn>(fan, FanGetZeroRpmState)(fan, out byte on) == 0 ? on != 0 : null;
            }
            catch (Exception) { return null; }
        }

        internal bool TrySetZeroRpm(bool enabled)
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return false;
            try
            {
                return AmdAdlxInterop.Fn<SetBoolFn>(fan, FanSetZeroRpmState)(fan, enabled ? (byte)1 : (byte)0) == 0;
            }
            catch (Exception) { return false; }
        }

        private static bool TrySet(IntPtr iface, int slot, int value)
        {
            if (iface == IntPtr.Zero) return false;
            try { return AmdAdlxInterop.Fn<SetIntFn>(iface, slot)(iface, value) == 0; }
            catch (Exception) { return false; }
        }

        private static void SafeRelease(IntPtr iface)
        {
            try { AmdAdlxInterop.Fn<ReleaseFn>(iface, IfaceRelease)(iface); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (IntPtr iface in new[] { _gfx2, _vram2, _fan, _fan1, _power })
                if (iface != IntPtr.Zero) SafeRelease(iface);
            _gfx2 = _vram2 = _fan = _fan1 = _power = IntPtr.Zero;
            if (_gpu != IntPtr.Zero) SafeRelease(_gpu);
        }
    }
}
