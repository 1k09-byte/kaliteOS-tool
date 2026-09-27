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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// One AMD adapter's 3D settings, reached through the shared ADLX system.
    ///
    /// Every read and write here is a real ADLX call against the driver: nothing
    /// is inferred from the card's name, and a toggle's shown state is always a
    /// fresh read-back rather than what the user clicked. Writes return the raw
    /// ADLX_RESULT so the page can show the driver's own verdict.
    ///
    /// Interfaces are reference counted. This session holds one on the GPU and
    /// one on the 3D settings services, plus one on each feature interface it
    /// touches, and gives them all back in Dispose.
    /// </summary>
    internal sealed class AmdRadeon3DSession : IDisposable
    {
        // ---- feature vtable slots ----------------------------------------------
        // Every IADLX3D* feature interface opens with Acquire/Release/
        // QueryInterface, so IsSupported and IsEnabled land on the same two
        // slots for all seven of them.
        private const int FeatureIsSupported = 3;
        private const int FeatureIsEnabled = 4;

        // IADLX3DAntiLag
        private const int AntiLagSetEnabled = 5;

        // IADLX3DEnhancedSync
        private const int EnhancedSyncSetEnabled = 5;

        // IADLX3DChill: 5 GetFPSRange, 6 GetMinFPS, 7 GetMaxFPS,
        //               8 SetEnabled, 9 SetMinFPS, 10 SetMaxFPS
        private const int ChillGetMinFPS = 6;
        private const int ChillGetMaxFPS = 7;
        private const int ChillSetEnabled = 8;
        private const int ChillSetMinFPS = 9;
        private const int ChillSetMaxFPS = 10;

        // IADLX3DBoost: 5 GetResolutionRange, 6 GetResolution, 7 SetEnabled, 8 SetResolution
        private const int BoostSetEnabled = 7;

        // IADLX3DImageSharpening: 5 GetSharpnessRange, 6 GetSharpness, 7 SetEnabled, 8 SetSharpness
        private const int ImageSharpeningSetEnabled = 7;

        // IADLX3DFrameRateTargetControl: 5 GetFPSRange, 6 GetFPS, 7 SetEnabled, 8 SetFPS
        private const int FrameRateTargetControlSetEnabled = 7;

        // IADLX3DRadeonSuperResolution: 5 SetEnabled, 6 GetSharpnessRange,
        //                             7 GetSharpness, 8 SetSharpness
        // Note SetEnabled comes BEFORE the getters here, unlike its siblings.
        private const int SuperResolutionSetEnabled = 5;

        // IADLX3DSettingsChangedHandling
        private const int ChangedHandlingAddListener = 3;
        private const int ChangedHandlingRemoveListener = 4;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBoolFn(IntPtr self, out byte value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetBoolFn(IntPtr self, byte value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetIntRangeFn(IntPtr self, out AdlxIntRange range);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetIntFn(IntPtr self, out int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetIntFn(IntPtr self, int value);

        private readonly IntPtr _gpu;
        private readonly IntPtr _settings;
        private readonly Dictionary<Radeon3DSetting, IntPtr> _features = new();

        private IntPtr _changedHandling;
        private IntPtr _listener;
        private bool _listening;
        private bool _disposed;

        internal AmdRadeon3DSession(IntPtr gpu, IntPtr settings3d, AmdGpuIdentity identity)
        {
            _gpu = gpu;
            _settings = settings3d;
            Identity = identity;
        }

        /// <summary>How the adapter is labelled in the picker.</summary>
        internal AmdGpuIdentity Identity { get; }

        internal bool IsUsable => !_disposed && _gpu != IntPtr.Zero && _settings != IntPtr.Zero;

        /// <summary>
        /// Raised when the driver says a 3D setting changed - typically because
        /// AMD's own Adrenalin software moved it. Raised on a driver thread, so
        /// the page has to marshal it before touching a control.
        /// </summary>
        internal event Action? SettingsChangedExternally;

        // ---- feature interface acquisition --------------------------------------

        /// <summary>
        /// Which IADLX3DSettingsServices slot hands out this feature. Radeon Super
        /// Resolution is the odd one: its getter takes no IADLXGPU, because the
        /// driver treats it as a property of the display, not of the adapter.
        /// </summary>
        private static int ServiceSlot(Radeon3DSetting setting) => setting switch
        {
            Radeon3DSetting.AntiLag => 3,
            Radeon3DSetting.Chill => 4,
            Radeon3DSetting.Boost => 5,
            Radeon3DSetting.ImageSharpening => 6,
            Radeon3DSetting.EnhancedSync => 7,
            Radeon3DSetting.FrameRateTargetControl => 9,
            Radeon3DSetting.RadeonSuperResolution => 14,
            _ => -1,
        };

        private static bool TakesGpuHandle(Radeon3DSetting setting)
            => setting != Radeon3DSetting.RadeonSuperResolution;

        /// <summary>The feature interface, acquired once and kept until Dispose.</summary>
        private IntPtr Feature(Radeon3DSetting setting)
        {
            if (!IsUsable) return IntPtr.Zero;
            if (_features.TryGetValue(setting, out IntPtr cached)) return cached;

            int slot = ServiceSlot(setting);
            if (slot < 0) return IntPtr.Zero;

            IntPtr feature = IntPtr.Zero;
            try
            {
                if (TakesGpuHandle(setting))
                {
                    if (AmdAdlxInterop.Fn<AmdAdlxInterop.GetPerGpuFeatureFn>(_settings, slot)(
                            _settings, _gpu, out feature) != 0)
                        feature = IntPtr.Zero;
                }
                else if (AmdAdlxInterop.Fn<AmdAdlxInterop.GetDisplayFeatureFn>(_settings, slot)(
                             _settings, out feature) != 0)
                {
                    feature = IntPtr.Zero;
                }
            }
            catch (Exception)
            {
                feature = IntPtr.Zero;
            }

            if (feature == IntPtr.Zero) return IntPtr.Zero;

            _features[setting] = feature;
            return feature;
        }

        // ---- reads ---------------------------------------------------------------

        /// <summary>
        /// What the driver says about this feature on this adapter: true, false,
        /// or null when the interface could not be reached at all (an older
        /// driver that does not implement the interface, rather than one that
        /// implements it and says no). Callers treat null like false for display
        /// but say the honest reason.
        /// </summary>
        internal bool? ReadSupported(Radeon3DSetting setting)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetBoolFn>(feature, FeatureIsSupported)(feature, out byte ok) == 0
                    ? ok != 0
                    : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>True / false, or null when the driver would not say.</summary>
        internal bool? ReadEnabled(Radeon3DSetting setting)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return null;
            try
            {
                return AmdAdlxInterop.Fn<GetBoolFn>(feature, FeatureIsEnabled)(feature, out byte on) == 0
                    ? on != 0
                    : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The driver's own value range for a feature's parameter - min, max and
        /// step as the driver reports them. Null when the feature has no range or
        /// the driver declined to give one. Nothing on this page substitutes a
        /// hardcoded range for a null here.
        /// </summary>
        internal AdlxIntRange? ReadRange(Radeon3DSetting setting) => ReadRange(
            setting,
            Radeon3DCatalog.Info(setting).Parameter);

        internal AdlxIntRange? ReadRange(Radeon3DSetting setting, Radeon3DParameter parameter)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return null;

            int slot = parameter switch
            {
                Radeon3DParameter.MinFps or Radeon3DParameter.MaxFps => 5, // Chill GetFPSRange
                Radeon3DParameter.TargetFps => 5,                            // FrameRateTargetControl GetFPSRange
                Radeon3DParameter.Resolution => 5,                          // Boost GetResolutionRange
                Radeon3DParameter.Sharpness => setting == Radeon3DSetting.RadeonSuperResolution
                    ? 6                                                      // Super Resolution GetSharpnessRange
                    : 5,                                                     // ImageSharpening GetSharpnessRange
                _ => -1,
            };
            if (slot < 0) return null;

            try
            {
                return AmdAdlxInterop.Fn<GetIntRangeFn>(feature, slot)(feature, out AdlxIntRange range) == 0
                    ? range
                    : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The feature's current parameter value, or null if unreadable.</summary>
        internal int? ReadValue(Radeon3DSetting setting, Radeon3DParameter parameter)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return null;

            int slot = (setting, parameter) switch
            {
                (Radeon3DSetting.Chill, Radeon3DParameter.MinFps) => ChillGetMinFPS,
                (Radeon3DSetting.Chill, Radeon3DParameter.MaxFps) => ChillGetMaxFPS,
                (Radeon3DSetting.FrameRateTargetControl, Radeon3DParameter.TargetFps) => 6,
                (Radeon3DSetting.ImageSharpening, Radeon3DParameter.Sharpness) => 6,
                (Radeon3DSetting.RadeonSuperResolution, Radeon3DParameter.Sharpness) => 7,
                (Radeon3DSetting.Boost, Radeon3DParameter.Resolution) => 6,
                _ => -1,
            };
            if (slot < 0) return null;

            try
            {
                return AmdAdlxInterop.Fn<GetIntFn>(feature, slot)(feature, out int value) == 0 ? value : null;
            }
            catch (Exception) { return null; }
        }

        // ---- writes --------------------------------------------------------------
        // Each returns the raw ADLX_RESULT: 0 ADLX_OK, 1 ADLX_ALREADY_ENABLED,
        // 12 ADLX_NOT_SUPPORTED, and so on. See RadeonResult for the wording.

        /// <summary>Turns a feature on or off. Returns the driver's ADLX_RESULT.</summary>
        internal int WriteEnabled(Radeon3DSetting setting, bool enable)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return RadeonResult.AdlxInvalidObject;

            int slot = setting switch
            {
                Radeon3DSetting.AntiLag => AntiLagSetEnabled,
                Radeon3DSetting.EnhancedSync => EnhancedSyncSetEnabled,
                Radeon3DSetting.Chill => ChillSetEnabled,
                Radeon3DSetting.Boost => BoostSetEnabled,
                Radeon3DSetting.ImageSharpening => ImageSharpeningSetEnabled,
                Radeon3DSetting.FrameRateTargetControl => FrameRateTargetControlSetEnabled,
                Radeon3DSetting.RadeonSuperResolution => SuperResolutionSetEnabled,
                _ => -1,
            };
            if (slot < 0) return RadeonResult.AdlxInvalidObject;

            try
            {
                return AmdAdlxInterop.Fn<SetBoolFn>(feature, slot)(feature, enable ? (byte)1 : (byte)0);
            }
            catch (Exception)
            {
                return RadeonResult.AdlxInvalidObject;
            }
        }

        /// <summary>Writes a feature's parameter. Returns the driver's ADLX_RESULT.</summary>
        internal int WriteValue(Radeon3DSetting setting, Radeon3DParameter parameter, int value)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return RadeonResult.AdlxInvalidObject;

            int slot = (setting, parameter) switch
            {
                (Radeon3DSetting.Chill, Radeon3DParameter.MinFps) => ChillSetMinFPS,
                (Radeon3DSetting.Chill, Radeon3DParameter.MaxFps) => ChillSetMaxFPS,
                (Radeon3DSetting.FrameRateTargetControl, Radeon3DParameter.TargetFps) => 8,
                (Radeon3DSetting.ImageSharpening, Radeon3DParameter.Sharpness) => 8,
                (Radeon3DSetting.RadeonSuperResolution, Radeon3DParameter.Sharpness) => 8,
                (Radeon3DSetting.Boost, Radeon3DParameter.Resolution) => 8,
                _ => -1,
            };
            if (slot < 0) return RadeonResult.AdlxInvalidObject;

            try
            {
                return AmdAdlxInterop.Fn<SetIntFn>(feature, slot)(feature, value);
            }
            catch (Exception)
            {
                return RadeonResult.AdlxInvalidObject;
            }
        }

        // ---- external change notification ----------------------------------------

        /// <summary>
        /// Subscribes to the driver's 3D settings changed event, so a change made
        /// in AMD's own Adrenalin software refreshes this page instead of leaving
        /// a stale toggle. Idempotent, and a driver that does not support it is
        /// not an error - it just returns false.
        /// </summary>
        internal bool TryStartListening()
        {
            if (_listening || !IsUsable) return _listening;

            try
            {
                if (_changedHandling == IntPtr.Zero)
                {
                    if (AmdAdlxInterop.Fn<AmdAdlxInterop.GetDisplayFeatureFn>(
                            _settings, AmdAdlxInterop.Settings3DGet3DSettingsChangedHandling)(
                            _settings, out _changedHandling) != 0
                        || _changedHandling == IntPtr.Zero)
                        return false;
                }

                if (_listener == IntPtr.Zero)
                    _listener = AmdAdlxInterop.CreateListenerObject();

                if (AmdAdlxInterop.Fn<AmdAdlxInterop.Add3DSettingsEventListenerFn>(_changedHandling, ChangedHandlingAddListener)(
                        _changedHandling, _listener) != 0)
                {
                    return false;
                }

                AmdAdlxInterop.AddListener(this);
                _listening = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Called by the interop from the driver's thread. Must not block.</summary>
        internal void NotifyChanged()
        {
            try { SettingsChangedExternally?.Invoke(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_listening && _changedHandling != IntPtr.Zero && _listener != IntPtr.Zero)
            {
                try
                {
                    AmdAdlxInterop.Fn<AmdAdlxInterop.Remove3DSettingsEventListenerFn>(
                        _changedHandling, ChangedHandlingRemoveListener)(_changedHandling, _listener);
                }
                catch (Exception) { }
                AmdAdlxInterop.RemoveListener(this);
            }
            _listening = false;

            // The listener object is ours: the driver only ever held the pointer.
            if (_listener != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_listener);
                _listener = IntPtr.Zero;
            }

            foreach (IntPtr feature in _features.Values)
                AmdAdlxInterop.SafeRelease(feature);
            _features.Clear();

            AmdAdlxInterop.SafeRelease(_changedHandling);
            _changedHandling = IntPtr.Zero;

            AmdAdlxInterop.SafeRelease(_settings);
            AmdAdlxInterop.SafeRelease(_gpu);
        }
    }
}
