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
using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// ADLX_IntRange - three adlx_int, nothing else. Layout matters: it is
    /// written straight into by the driver, so it must match the header exactly
    /// (SDK/Include/ADLXStructures.h).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlxIntRange
    {
        public int MinValue;
        public int MaxValue;
        public int Step;

        public override string ToString() => $"{MinValue}..{MaxValue} step {Step}";
    }

    /// <summary>
    /// A live handle on one AMD adapter, with the manual-tuning domains reached
    /// from it. This is the write path's entry point.
    ///
    /// Two things make ADLX safe to drive from here:
    ///
    /// 1. The domains are fetched BY INTERFACE ID, not by casting. The graphics
    ///    domain ships as two unrelated interfaces - IADLXManualGraphicsTuning1
    ///    (tuning-state lists) and IADLXManualGraphicsTuning2 (scalar frequency
    ///    and voltage) - and both derive straight from IADLXInterface, so the
    ///    object GetManualGFXTuning hands back has one of two different vtables
    ///    depending on which class the driver instantiated. QueryInterface with
    ///    the IID pins the vtable to the generation we actually read, instead of
    ///    trusting a layout we cannot see. (VRAM has the same split; fan and
    ///    power are single chains, where a derived generation simply appends.)
    ///
    /// 2. Every interface is reference counted (Acquire/Release at vtable slots
    ///    0 and 1) and released in Dispose. ADLX's own docs require it; leaking
    ///    a driver interface per detection would slowly bleed GPU memory.
    ///
    /// Units, straight from the header docs, because they are not uniform:
    ///   - core max frequency ....... MHz, but an OFFSET from Navi4+ (absolute before)
    ///   - max VRAM frequency ....... MHz, always ABSOLUTE
    ///   - power limit .............. percent (already, which is why it maps 1:1)
    ///   - fan speed ................ percent
    /// </summary>
    internal sealed class AmdGpuTuningSession : IDisposable
    {
        private readonly IntPtr _gpu;
        private readonly IntPtr _tuning;
        private IntPtr _gfx2;
        private IntPtr _vram2;
        private IntPtr _fan;
        private IntPtr _power;
        private bool _disposed;

        internal AmdGpuTuningSession(IntPtr gpu, IntPtr tuningServices, AmdGpuTuningSupport support)
        {
            _gpu = gpu;
            _tuning = tuningServices;
            Support = support;
        }

        /// <summary>What the capability queries said about this adapter.</summary>
        internal AmdGpuTuningSupport Support { get; }

        internal bool IsUsable => !_disposed && _gpu != IntPtr.Zero;

        // ---- IADLXGPUTuningServices vtable -------------------------------------
        // IGPUTuning.h: IADLXGPUTuningServicesVtbl, slots after Acquire/Release/QueryInterface.
        private const int TuningGetManualGfx = 14;
        private const int TuningGetManualVram = 15;
        private const int TuningGetManualFan = 16;
        private const int TuningGetManualPower = 17;

        // ---- IADLXInterface vtable ---------------------------------------------
        private const int IfaceRelease = 1;
        private const int IfaceQueryInterface = 2;

        // ---- IADLXManualGraphicsTuning2 (scalar frequency/voltage) --------------
        private const int Gfx2GetMaxFrequencyRange = 6;
        private const int Gfx2GetMaxFrequency = 7;
        private const int Gfx2SetMaxFrequency = 8;

        // ---- IADLXManualVRAMTuning2 (absolute max VRAM frequency) ---------------
        private const int Vram2GetMaxFrequencyRange = 7;
        private const int Vram2GetMaxFrequency = 8;
        private const int Vram2SetMaxFrequency = 9;

        // ---- IADLXManualPowerTuning (percent) ----------------------------------
        private const int PowerGetLimitRange = 3;
        private const int PowerGetLimit = 4;
        private const int PowerSetLimit = 5;

        // ---- IADLXManualFanTuning ----------------------------------------------
        private const int FanGetTuningRanges = 3;
        private const int FanGetTuningStates = 4;
        private const int FanIsSupportedZeroRpm = 8;
        private const int FanGetZeroRpmState = 9;
        private const int FanSetZeroRpmState = 10;

        // ---- IADLXManualFanTuningState / StateList -----------------------------
        private const int FanStateGetSpeed = 3;
        private const int FanStateSetSpeed = 4;
        private const int FanStateListSize = 3;
        private const int FanStateListAt = 11;

        private const string IidGraphicsTuning2 = "IADLXManualGraphicsTuning2";
        private const string IidVramTuning2 = "IADLXManualVRAMTuning2";
        private const string IidFanTuning = "IADLXManualFanTuning";
        private const string IidPowerTuning = "IADLXManualPowerTuning";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long AcquireFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, IntPtr interfaceId, out IntPtr ppInterface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDomainFn(IntPtr self, IntPtr gpu, out IntPtr ppDomain);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntRangeFn(IntPtr self, out AdlxIntRange range);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntRange2Fn(IntPtr self, out AdlxIntRange first, out AdlxIntRange second);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetIntFn(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetIntFn(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBoolFn(IntPtr self, out byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBoolFn(IntPtr self, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObjectFn(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ListAtStateFn(IntPtr self, uint location, out IntPtr ppState);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ListCountFn(IntPtr self);

        // ---- domain acquisition -------------------------------------------------

        /// <summary>
        /// Resolves one manual-tuning domain, pinned to the IID. Returns
        /// IntPtr.Zero when the driver does not offer that generation, which is
        /// normal: VRAMTuning2 is Navi-era and later, older cards stop at
        /// VRAMTuning1 (timing tables) and have no scalar frequency call at all.
        /// </summary>
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

                    // The object we were handed is the caller's reference; the
                    // versioned pointer is a new one we own.
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
        private IntPtr Power => _power != IntPtr.Zero ? _power : (_power = Domain(TuningGetManualPower, IidPowerTuning));

        // ---- core clock (MHz; offset from Navi4+, absolute before) -------------

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

        // ---- VRAM (MHz, absolute) ----------------------------------------------

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

        // ---- power limit (percent) ---------------------------------------------

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

        // ---- fan (percent) ------------------------------------------------------

        /// <summary>The driver's own fan-speed range, in percent.</summary>
        internal AdlxIntRange? ReadFanSpeedRange()
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return null;
            try
            {
                // The second range out is the fan-curve temperature threshold,
                // which is a different control and deliberately dropped.
                return AmdAdlxInterop.Fn<GetIntRange2Fn>(fan, FanGetTuningRanges)(fan, out AdlxIntRange speed, out _) == 0
                    ? speed : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Applies a static fan speed. ADLX has no scalar "set fan speed" on the
        /// tuning interface - the value lives on a fan tuning state, so this
        /// walks the state list, mutates the first state, and puts it back.
        /// </summary>
        internal bool TrySetFanSpeed(int percent)
        {
            IntPtr fan = Fan;
            if (fan == IntPtr.Zero) return false;
            IntPtr list = IntPtr.Zero, state = IntPtr.Zero;
            try
            {
                if (AmdAdlxInterop.Fn<GetObjectFn>(fan, FanGetTuningStates)(fan, out list) != 0 || list == IntPtr.Zero)
                    return false;

                uint count = AmdAdlxInterop.Fn<ListCountFn>(list, FanStateListSize)(list);
                if (count == 0) return false;

                if (AmdAdlxInterop.Fn<ListAtStateFn>(list, FanStateListAt)(list, 0, out state) != 0 || state == IntPtr.Zero)
                    return false;

                return AmdAdlxInterop.Fn<SetIntFn>(state, FanStateSetSpeed)(state, percent) == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (state != IntPtr.Zero) SafeRelease(state);
                if (list != IntPtr.Zero) SafeRelease(list);
            }
        }

        /// <summary>
        /// The current static fan speed, or null. Used as the revert anchor: if
        /// we cannot read the fan state we refuse to arm a fan revert rather
        /// than guess a value to restore.
        /// </summary>
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

        // ---- Zero RPM -------------------------------------------------------------

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

        // ---- helpers --------------------------------------------------------------

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
            foreach (IntPtr iface in new[] { _gfx2, _vram2, _fan, _power })
                if (iface != IntPtr.Zero) SafeRelease(iface);
            _gfx2 = _vram2 = _fan = _power = IntPtr.Zero;
        }
    }
}
