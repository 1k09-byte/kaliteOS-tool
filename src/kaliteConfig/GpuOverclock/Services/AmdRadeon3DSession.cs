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
    internal sealed class AmdRadeon3DSession : IDisposable
    {
        private const int FeatureIsSupported = 3;
        private const int FeatureIsEnabled = 4;

        private const int AntiLagSetEnabled = 5;

        private const int EnhancedSyncSetEnabled = 5;

        private const int ChillGetMinFPS = 6;
        private const int ChillGetMaxFPS = 7;
        private const int ChillSetEnabled = 8;
        private const int ChillSetMinFPS = 9;
        private const int ChillSetMaxFPS = 10;

        private const int BoostSetEnabled = 7;

        private const int ImageSharpeningSetEnabled = 7;

        private const int FrameRateTargetControlSetEnabled = 7;

        private const int SuperResolutionSetEnabled = 5;

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

        internal AmdGpuIdentity Identity { get; }

        internal bool IsUsable => !_disposed && _gpu != IntPtr.Zero && _settings != IntPtr.Zero;

        internal event Action? SettingsChangedExternally;

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

        internal AdlxIntRange? ReadRange(Radeon3DSetting setting) => ReadRange(
            setting,
            Radeon3DCatalog.Info(setting).Parameter);

        internal AdlxIntRange? ReadRange(Radeon3DSetting setting, Radeon3DParameter parameter)
        {
            IntPtr feature = Feature(setting);
            if (feature == IntPtr.Zero) return null;

            int slot = parameter switch
            {
                Radeon3DParameter.MinFps or Radeon3DParameter.MaxFps => 5,
                Radeon3DParameter.TargetFps => 5,
                Radeon3DParameter.Resolution => 5,
                Radeon3DParameter.Sharpness => setting == Radeon3DSetting.RadeonSuperResolution
                    ? 6
                    : 5,
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
