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
    /// <summary>
    /// The Adrenalin-style 3D features, reached through ADLX's 3D settings
    /// services - the same AMD driver the Overclock tab already talks to.
    ///
    /// There is exactly one ADLX system in this process. <see cref="AmdAdlxInterop"/>
    /// owns the single ADLXInitialize / ADLXTerminate pair in the app, and this
    /// is a partial of it, so the Radeon tab takes its services from that same
    /// IADLXSystem instance under that same Gate. Nothing here calls
    /// ADLXInitialize or ADLXTerminate: a second Initialize is how you get a
    /// driver that refuses the second system, and a Terminate from the wrong
    /// side tears down the Overclock tab's handles with it.
    ///
    /// The adapters are the same IADLXGPU objects the tuning path already holds:
    /// both walk IADLXSystem::GetGPUs on the same system, which returns one
    /// shared interface per adapter. Each session takes its own Acquire
    /// (vtable slot 0) and releases it in Dispose, exactly like
    /// AmdGpuTuningSession does.
    ///
    /// Vtable indices come from the AMD headers in
    /// https://github.com/GPUOpen-LibrariesAndSDKs/ADLX (SDK/Include) - ISystem.h
    /// for the system services, I3DSettings.h for the 3D services and the
    /// change listener. As in the sibling file they are pinned in one place,
    /// because an index off by one returns a plausible wrong value instead of
    /// crashing and there is no way to notice at the call site.
    /// </summary>
    internal static partial class AmdAdlxInterop
    {
        // ---- IADLXSystem vtable ------------------------------------------------
        // ISystem.h IADLXSystemVtbl, after the three IADLXInterface slots:
        //   0 GetHybridGraphicsType    5 GetGPUsChangedHandling     9 GetPerformanceMonitoringServices
        //   1 GetGPUs                  6 EnableLog                  10 TotalSystemRAM
        //   2 QueryInterface           7 Get3DSettingsServices      11 GetI2C
        //   3 GetDisplaysServices      8 GetGPUTuningServices
        //   4 GetDesktopsServices
        // Get3DSettingsServices sits immediately below the tuning slot used by
        // EnumerateTuningSessions, which is the cross-check that this table is
        // offset correctly.
        private const int SystemGet3DSettingsServices = 7;

        // ---- IADLX3DSettingsServices vtable ------------------------------------
        // I3DSettings.h IADLX3DSettingsServicesVtbl, after Acquire/Release/QueryInterface.
        private const int Settings3DGetAntiLag = 3;
        private const int Settings3DGetChill = 4;
        private const int Settings3DGetBoost = 5;
        private const int Settings3DGetImageSharpening = 6;
        private const int Settings3DGetEnhancedSync = 7;
        private const int Settings3DGetFrameRateTargetControl = 9;
        private const int Settings3DGetRadeonSuperResolution = 14;

        /// <summary>Get3DSettingsChangedHandling - also a no-GPU getter, so it is read
        /// through the same delegate as Super Resolution.</summary>
        internal const int Settings3DGet3DSettingsChangedHandling = 16;

        // ---- IADLXInterface vtable ---------------------------------------------
        private const int IfaceRelease = 1;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Get3DSettingsServicesFn(IntPtr self, out IntPtr ppServices);

        /// <summary>Per-GPU features: GetAntiLag(gpu, out), GetChill(gpu, out), ...</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetPerGpuFeatureFn(IntPtr self, IntPtr gpu, out IntPtr ppFeature);

        /// <summary>Display-level features: GetRadeonSuperResolution(out) takes no GPU.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetDisplayFeatureFn(IntPtr self, out IntPtr ppFeature);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long ReleaseFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int Add3DSettingsEventListenerFn(IntPtr self, IntPtr listener);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int Remove3DSettingsEventListenerFn(IntPtr self, IntPtr listener);

        /// <summary>
        /// Opens a 3D settings session on every AMD adapter. The caller owns the
        /// sessions and must dispose them. An empty list means ADLX is unavailable
        /// or the driver reported no adapters, which is normal, not an error.
        /// </summary>
        public static List<AmdRadeon3DSession> Enumerate3DSettingsSessions()
        {
            var sessions = new List<AmdRadeon3DSession>();
            lock (Gate)
            {
                // The one initialisation, already done or done right here.
                if (!EnsureInitialized()) return sessions;

                IntPtr settings3d = IntPtr.Zero;
                try
                {
                    if (Fn<Get3DSettingsServicesFn>(_system, SystemGet3DSettingsServices)(
                            _system, out settings3d) != 0
                        || settings3d == IntPtr.Zero)
                        return sessions;

                    // Our own reference on the services object, given back in the
                    // finally below. Each session takes one of its own on top.
                    AddRef(settings3d);

                    foreach (IntPtr gpu in AcquireGpuHandles())
                    {
                        AmdRadeon3DSession? session = null;
                        bool tookServicesRef = false;
                        try
                        {
                            // Read first, reference second: if either of these
                            // throws there is nothing yet to give back.
                            AmdGpuIdentity identity = ReadIdentity(gpu);

                            // The session takes its OWN reference on the shared
                            // services object. There is one services object and
                            // one session per adapter, so a single Acquire shared
                            // between them would be released once per adapter by
                            // their Disposes - an over-release into the driver's
                            // own reference count.
                            AddRef(settings3d);
                            tookServicesRef = true;
                            session = new AmdRadeon3DSession(gpu, settings3d, identity);
                        }
                        catch (Exception)
                        {
                            // One unreadable adapter must not hide the others.
                            if (tookServicesRef) SafeRelease(settings3d);
                        }

                        if (session is not null) sessions.Add(session);
                        else SafeRelease(gpu);
                    }
                }
                catch (Exception)
                {
                    // A malformed vtable read must not take the app down; an empty
                    // list simply means the Radeon tab shows nothing.
                }
                finally
                {
                    SafeRelease(settings3d);
                }
            }
            return sessions;
        }

        /// <summary>
        /// The IADLXGPU handles from the already-initialized system, each with a
        /// reference the caller owns. This is the same list the tuning path walks
        /// - one GetGPUs call, one interface per adapter - so the Radeon tab and
        /// the Overclock tab are looking at the same GPU objects.
        /// </summary>
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
                // A bad vtable read partway through would otherwise leak the
                // references already taken; the caller gets the adapters that
                // did enumerate, which is the same best-effort posture
                // EnumerateTuningSessions takes.
                foreach (IntPtr gpu in handles) SafeRelease(gpu);
                handles.Clear();
            }
            return handles;
        }

        /// <summary>Name / PNP id / discrete flag, without asking about tuning.</summary>
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

        /// <summary>
        /// Take a reference at vtable slot 0. ADLX interfaces are reference
        /// counted, so an object handed to more than one owner needs one Acquire
        /// per owner - a single shared Acquire released N times is an over-release
        /// into the driver's own bookkeeping.
        /// </summary>
        internal static void AddRef(IntPtr iface)
        {
            if (iface == IntPtr.Zero) return;
            try { Fn<AcquireFn>(iface, IfaceAcquire)(iface); }
            catch (Exception) { }
        }

        /// <summary>Release at vtable slot 1, for the handles this file acquires.</summary>
        internal static void SafeRelease(IntPtr iface)
        {
            if (iface == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(iface, IfaceRelease)(iface); }
            catch (Exception) { }
        }

        // ---- change notification -------------------------------------------------
        // IADLX3DSettingsChangedListenerVtbl has exactly one slot,
        // On3DSettingsChanged, and it is NOT an IADLXInterface: no Acquire, no
        // Release, nothing the driver owns. So the object is one pointer wide, we
        // allocate it, the driver keeps the pointer until we unregister, and we
        // free it. The vtable and its function pointer live for the process, so
        // every registered listener can share them.

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate byte On3DSettingsChangedFn(IntPtr self, IntPtr changedEvent);

        private static readonly On3DSettingsChangedFn s_trampoline;
        private static readonly IntPtr s_listenerVtbl;

        /// <summary>
        /// Sessions with a listener registered right now. Normally one; a list
        /// rather than a single field so that two adapters open at once both get
        /// their refresh, and so registration cannot leak a stale session.
        /// </summary>
        private static readonly List<AmdRadeon3DSession> s_listening = new();

        static AmdAdlxInterop()
        {
            // Declared in an explicit static constructor because the vtable holds
            // the address of the delegate, and the delegate has to exist first.
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
                // Throwing back out through a driver frame would be far worse than
                // missing one refresh; the next manual refresh still corrects it.
            }
            return 1; // adlx_bool: handled
        }

        /// <summary>Builds the listener object the driver will hold a raw pointer to.</summary>
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

    /// <summary>
    /// Just enough of an adapter to label it in the UI. Deliberately not
    /// <see cref="AmdGpuTuningSupport"/>: the Radeon tab has nothing to say about
    /// clock domains, and asking the tuning services for them would be a lie
    /// about what this page uses.
    /// </summary>
    internal sealed record AmdGpuIdentity(string Name, string PnpString, bool IsDiscrete);
}
