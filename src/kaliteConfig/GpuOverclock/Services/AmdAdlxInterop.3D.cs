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

namespace kaliteConfig.GpuOverclock.Services
{
    internal static partial class AmdAdlxInterop
    {
        private const int SystemGet3DSettingsServices = 7;

        private const int Settings3DGetAntiLag = 3;
        private const int Settings3DGetChill = 4;
        private const int Settings3DGetBoost = 5;
        private const int Settings3DGetImageSharpening = 6;
        private const int Settings3DGetEnhancedSync = 7;
        private const int Settings3DGetFrameRateTargetControl = 9;
        private const int Settings3DGetRadeonSuperResolution = 14;

        internal const int Settings3DGet3DSettingsChangedHandling = 16;

        private const int IfaceRelease = 1;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Get3DSettingsServicesFn(IntPtr self, out IntPtr ppServices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetPerGpuFeatureFn(IntPtr self, IntPtr gpu, out IntPtr ppFeature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetDisplayFeatureFn(IntPtr self, out IntPtr ppFeature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long ReleaseFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int Add3DSettingsEventListenerFn(IntPtr self, IntPtr listener);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int Remove3DSettingsEventListenerFn(IntPtr self, IntPtr listener);

        public static List<AmdRadeon3DSession> Enumerate3DSettingsSessions()
        {
            var sessions = new List<AmdRadeon3DSession>();
            lock (Gate)
            {
                if (!EnsureInitialized()) return sessions;

                IntPtr settings3d = IntPtr.Zero;
                try
                {
                    if (Fn<Get3DSettingsServicesFn>(_system, SystemGet3DSettingsServices)(
                            _system, out settings3d) != 0
                        || settings3d == IntPtr.Zero)
                        return sessions;

                    AddRef(settings3d);

                    foreach (IntPtr gpu in AcquireGpuHandles())
                    {
                        AmdRadeon3DSession? session = null;
                        bool tookServicesRef = false;
                        try
                        {
                            AmdGpuIdentity identity = ReadIdentity(gpu);

                            AddRef(settings3d);
                            tookServicesRef = true;
                            session = new AmdRadeon3DSession(gpu, settings3d, identity);
                        }
                        catch (Exception)
                        {
                            if (tookServicesRef) SafeRelease(settings3d);
                        }

                        if (session is not null) sessions.Add(session);
                        else SafeRelease(gpu);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    SafeRelease(settings3d);
                }
            }
            return sessions;
        }

        private static List<IntPtr> AcquireGpuHandles()
        {
            var handles = new List<IntPtr>();
            try
            {
                if (Fn<GetGpusFn>(_system, SystemGetGpus)(_system, out IntPtr list) != 0 || list == IntPtr.Zero)
                    return handles;

                uint count = Fn<ListSizeFn>(list, ListSize)(list);
                var at = Fn<ListAtGpuFn>(list, ListAtGpu);

                for (uint i = 0; i < count; i++)
                {
                    if (at(list, i, out IntPtr gpu) != 0 || gpu == IntPtr.Zero) continue;
                    Fn<AcquireFn>(gpu, IfaceAcquire)(gpu);
                    handles.Add(gpu);
                }
            }
            catch (Exception)
            {
                foreach (IntPtr gpu in handles) SafeRelease(gpu);
                handles.Clear();
            }
            return handles;
        }

        private static AmdGpuIdentity ReadIdentity(IntPtr gpu) => new(
            Name: ReadString(gpu, GpuName),
            PnpString: ReadString(gpu, GpuPnpString),
            IsDiscrete: IsDiscreteGpu(gpu));

        private static bool IsDiscreteGpu(IntPtr gpu)
        {
            try
            {
                return Fn<GpuIsExternalFn>(gpu, GpuIsExternal)(gpu, out byte external) == 0 && external != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void AddRef(IntPtr iface)
        {
            if (iface == IntPtr.Zero) return;
            try { Fn<AcquireFn>(iface, IfaceAcquire)(iface); }
            catch (Exception) { }
        }

        internal static void SafeRelease(IntPtr iface)
        {
            if (iface == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(iface, IfaceRelease)(iface); }
            catch (Exception) { }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate byte On3DSettingsChangedFn(IntPtr self, IntPtr changedEvent);

        private static readonly On3DSettingsChangedFn s_trampoline;
        private static readonly IntPtr s_listenerVtbl;

        private static readonly List<AmdRadeon3DSession> s_listening = new();

        static AmdAdlxInterop()
        {
            s_trampoline = On3DSettingsChanged;
            s_listenerVtbl = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(s_listenerVtbl, Marshal.GetFunctionPointerForDelegate(s_trampoline));
        }

        private static byte On3DSettingsChanged(IntPtr self, IntPtr changedEvent)
        {
            try
            {
                List<AmdRadeon3DSession> targets;
                lock (Gate) { targets = new List<AmdRadeon3DSession>(s_listening); }
                foreach (AmdRadeon3DSession session in targets)
                {
                    try { session.NotifyChanged(); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
            return 1;
        }

        internal static IntPtr CreateListenerObject()
        {
            IntPtr listener = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(listener, s_listenerVtbl);
            return listener;
        }

        internal static void AddListener(AmdRadeon3DSession session)
        {
            lock (Gate)
            {
                if (!s_listening.Contains(session)) s_listening.Add(session);
            }
        }

        internal static void RemoveListener(AmdRadeon3DSession session)
        {
            lock (Gate) { s_listening.Remove(session); }
        }
    }

    internal sealed record AmdGpuIdentity(string Name, string PnpString, bool IsDiscrete);
}
