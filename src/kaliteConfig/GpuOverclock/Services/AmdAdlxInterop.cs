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
    /// <summary>What ADLX reports about one AMD GPU's tuning abilities.</summary>
    public sealed record AmdGpuTuningSupport(
        string Name,
        string PnpString,
        bool IsDiscrete,
        uint VramMb,
        bool ManualGfx,
        bool ManualVram,
        bool ManualFan,
        bool ManualPower)
    {
        /// <summary>True when the card exposes at least one control we can drive.</summary>
        public bool AnyControlSupported => ManualGfx || ManualVram || ManualFan || ManualPower;
    }

    /// <summary>
    /// Managed binding to ADLX, AMD's device library.
    ///
    /// The important part is what this does NOT need. ADLX is not a library you
    /// build or ship: the AMD display driver installs it into System32 as
    /// amdadlx64.dll, exporting seven plain C entry points. Every other ADLX call
    /// goes through an interface that is just
    ///
    ///     struct IADLXSystem { const IADLXSystemVtbl* pVtbl; };
    ///
    /// with methods at fixed offsets in that vtable. So this is ordinary managed
    /// P/Invoke - no native shim to compile, no C++ toolchain in the build, and
    /// nothing of AMD's redistributed in our installer. On a machine with no AMD
    /// driver the DLL simply is not there and every entry point here degrades to
    /// "not available".
    ///
    /// Vtable indices below are copied from the AMD headers in
    /// https://github.com/GPUOpen-LibrariesAndSDKs/ADLX (SDK/Include) and were
    /// verified live against a real driver: an index off by one silently returns
    /// a plausible-looking wrong value rather than crashing, so they are pinned
    /// here in one place and nowhere else.
    /// </summary>
    internal static partial class AmdAdlxInterop
    {
        private const string Dll = "amdadlx64.dll";

        /// <summary>ADLX_MAKE_FULL_VER(2, 0, 0, 125) - matches the headers' v2.0.</summary>
        private const ulong RequestedVersion = (2UL << 48) | (0UL << 32) | (0UL << 16) | 125UL;

        // ---- entry points -----------------------------------------------------

        [DllImport(Dll, EntryPoint = "ADLXInitialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxInitialize(ulong version, out IntPtr ppSystem);

        [DllImport(Dll, EntryPoint = "ADLXTerminate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxTerminate();

        // Signature per ADLX.h: ADLX_RESULT ADLX_CDECL ADLXQueryVersion(const char** version).
        // It takes an OUT POINTER and returns a result code. Declaring it as
        // "returns IntPtr, takes nothing" passes no argument, so the callee
        // writes 8 bytes of a const char* through whatever happened to be in the
        // first argument register - silent memory corruption that took the app
        // down with an uncatchable ExecutionEngineException. Never infer an
        // export's shape: read the _Fn typedef in ADLX.h.
        [DllImport(Dll, EntryPoint = "ADLXQueryVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxQueryVersion(out IntPtr version);

        // ---- IADLXSystem vtable ------------------------------------------------
        private const int SystemGetGpus = 1;
        private const int SystemGetGpuTuningServices = 8;

        // ---- IADLXGPUList vtable -----------------------------------------------
        private const int ListSize = 3;
        private const int ListAtGpu = 11;

        // ---- IADLXGPU vtable ---------------------------------------------------
        private const int GpuType = 5;
        private const int GpuIsExternal = 6;
        private const int GpuName = 7;
        private const int GpuPnpString = 9;
        private const int GpuTotalVram = 11;

        // ---- IADLXGPUTuningServices vtable -------------------------------------
        private const int TuningIsSupportedManualGfx = 8;
        private const int TuningIsSupportedManualVram = 9;
        private const int TuningIsSupportedManualFan = 10;
        private const int TuningIsSupportedManualPower = 11;

        // ---- delegates ---------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetGpusFn(IntPtr self, out IntPtr ppGpuList);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetGpuTuningServicesFn(IntPtr self, out IntPtr ppServices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ListSizeFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ListAtGpuFn(IntPtr self, uint location, out IntPtr ppGpu);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GpuTypeFn(IntPtr self, out int type);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GpuIsExternalFn(IntPtr self, out byte isExternal);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GpuStringFn(IntPtr self, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GpuVramFn(IntPtr self, out uint vramMb);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IsSupportedFn(IntPtr self, IntPtr gpu, out byte supported);

        /// <summary>Acquire/Release sit at slots 0 and 1 of every ADLX vtable.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long AcquireFn(IntPtr self);

        private const int IfaceAcquire = 0;

        // ---- state -------------------------------------------------------------

        private static readonly object Gate = new();
        private static bool _initialized;
        private static IntPtr _system;

        /// <summary>True when the AMD driver on this machine provides ADLX.</summary>
        public static bool IsAvailable
        {
            get { lock (Gate) { EnsureInitialized(); return _initialized; } }
        }

        /// <summary>The version string the driver reports, or null when unavailable.</summary>
        public static string? DriverVersion
        {
            get
            {
                lock (Gate)
                {
                    if (!EnsureInitialized()) return null;
                    try
                    {
                        return AdlxQueryVersion(out IntPtr version) == 0 && version != IntPtr.Zero
                            ? Marshal.PtrToStringAnsi(version)
                            : null;
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        private static bool EnsureInitialized()
        {
            if (_initialized) return true;
            if (_system != IntPtr.Zero) return false; // attempted and failed; do not retry every call

            try
            {
                if (AdlxInitialize(RequestedVersion, out IntPtr system) == 0 && system != IntPtr.Zero)
                {
                    _system = system;
                    _initialized = true;
                }
            }
            catch (DllNotFoundException)
            {
                // No AMD driver installed. Entirely normal - not an error.
            }
            catch (EntryPointNotFoundException)
            {
                // An older driver shipped a different build. Also not fatal.
            }
            catch (BadImageFormatException)
            {
                // 32-bit process against the 64-bit-only amdadlx64.dll. Normal
                // when the app is built for x86 - not an error to surface.
            }

            return _initialized;
        }

        /// <summary>
        /// Enumerates AMD GPUs and asks the driver which tuning controls each one
        /// supports. Returns an empty list when ADLX is unavailable.
        /// </summary>
        public static List<AmdGpuTuningSupport> EnumerateGpusWithSupport()
        {
            var results = new List<AmdGpuTuningSupport>();
            foreach (AmdGpuTuningSession session in EnumerateTuningSessions())
            {
                results.Add(session.Support);
                session.Dispose();
            }
            return results;
        }

        /// <summary>
        /// Opens a tuning session on every AMD adapter, including the ones whose
        /// domains the driver refuses - the controller picks from these, and it
        /// needs to see the whole list to choose the right card. Each session
        /// holds a reference the CALLER must dispose.
        /// </summary>
        public static List<AmdGpuTuningSession> EnumerateTuningSessions()
        {
            var sessions = new List<AmdGpuTuningSession>();
            lock (Gate)
            {
                if (!EnsureInitialized()) return sessions;

                try
                {
                    if (Fn<GetGpusFn>(_system, SystemGetGpus)(_system, out IntPtr list) != 0 || list == IntPtr.Zero)
                        return sessions;

                    uint count = Fn<ListSizeFn>(list, ListSize)(list);
                    var at = Fn<ListAtGpuFn>(list, ListAtGpu);

                    Fn<GetGpuTuningServicesFn>(_system, SystemGetGpuTuningServices)(_system, out IntPtr tuning);

                    for (uint i = 0; i < count; i++)
                    {
                        if (at(list, i, out IntPtr gpu) != 0 || gpu == IntPtr.Zero) continue;
                        try
                        {
                            AmdGpuTuningSupport support = ReadSupport(gpu, tuning);

                            // Take our own reference so the handle stays valid
                            // after this call returns. ADLX interfaces are
                            // reference counted; not doing this and then using
                            // the pointer is a use-after-free.
                            Fn<AcquireFn>(gpu, IfaceAcquire)(gpu);
                            sessions.Add(new AmdGpuTuningSession(gpu, tuning, support));
                        }
                        catch (Exception)
                        {
                            // One unreadable adapter must not hide the others.
                        }
                    }
                }
                catch (Exception)
                {
                    // A malformed vtable read must not take the app down; an
                    // incomplete list simply means the AMD section shows nothing.
                }
            }
            return sessions;
        }

        private static AmdGpuTuningSupport ReadSupport(IntPtr gpu, IntPtr tuning)
        {
            string name = ReadString(gpu, GpuName);
            string pnp = ReadString(gpu, GpuPnpString);
            Fn<GpuTypeFn>(gpu, GpuType)(gpu, out _);
            Fn<GpuIsExternalFn>(gpu, GpuIsExternal)(gpu, out byte external);
            Fn<GpuVramFn>(gpu, GpuTotalVram)(gpu, out uint vramMb);

            return new AmdGpuTuningSupport(
                Name: name,
                PnpString: pnp,
                IsDiscrete: external != 0,
                VramMb: vramMb,
                ManualGfx: IsSupported(tuning, gpu, TuningIsSupportedManualGfx),
                ManualVram: IsSupported(tuning, gpu, TuningIsSupportedManualVram),
                ManualFan: IsSupported(tuning, gpu, TuningIsSupportedManualFan),
                ManualPower: IsSupported(tuning, gpu, TuningIsSupportedManualPower));
        }

        private static bool IsSupported(IntPtr tuning, IntPtr gpu, int index)
        {
            if (tuning == IntPtr.Zero) return false;
            return Fn<IsSupportedFn>(tuning, index)(tuning, gpu, out byte ok) == 0 && ok != 0;
        }

        private static string ReadString(IntPtr iface, int index)
        {
            try
            {
                if (Fn<GpuStringFn>(iface, index)(iface, out IntPtr p) != 0) return string.Empty;
                return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Releases ADLX. Safe to call when it was never initialized.</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
                if (!_initialized) return;
                try { AdlxTerminate(); } catch { }
                _initialized = false;
                _system = IntPtr.Zero;
            }
        }

        /// <summary>Reads a method pointer out of an ADLX interface's vtable.</summary>
        internal static T Fn<T>(IntPtr iface, int index) where T : Delegate
        {
            if (iface == IntPtr.Zero)
                throw new EntryPointNotFoundException($"ADLX interface for vtable slot {index} is null.");

            IntPtr vtbl = Marshal.ReadIntPtr(iface);
            if (vtbl == IntPtr.Zero)
                throw new EntryPointNotFoundException($"ADLX vtable for slot {index} is null.");

            IntPtr slot = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            if (slot == IntPtr.Zero)
                throw new EntryPointNotFoundException($"ADLX vtable slot {index} is null.");

            // A slot that is non-null but does not point into amdadlx64.dll is not
            // a function at all - it is a mis-indexed read (driver built against
            // a different vtable layout) or corrupted memory. Calling through it
            // is an immediate, *uncatchable* process kill (a bad call target is
            // not an exception the CLR can convert), so refuse it here and let
            // the caller's try/catch turn it into "no AMD controls". Validating
            // the target is the only defence: there is no SEH to catch.
            if (!PointsIntoAdlx(slot))
                throw new EntryPointNotFoundException(
                    $"ADLX vtable slot {index} points outside {Dll} - refusing to call it.");

            return Marshal.GetDelegateForFunctionPointer<T>(slot);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        /// <summary>Image bounds of amdadlx64.dll, or null when unmeasurable.</summary>
        private static (IntPtr Start, IntPtr End)? _adlxImage;

        /// <summary>
        /// True when <paramref name="p"/> falls inside the loaded amdadlx64.dll
        /// image. The bounds come from the PE headers already mapped in memory
        /// (Base + SizeOfImage), so this needs no extra API surface and no handle.
        /// If the module cannot be measured, returns true: refusing everything
        /// would disable the feature outright. This is a guard against garbage
        /// call targets, not a security boundary.
        /// </summary>
        private static bool PointsIntoAdlx(IntPtr p)
        {
            if (p == IntPtr.Zero) return false;

            var image = _adlxImage ??= MeasureAdlxImage();
            if (image is null) return true;

            return p >= image.Value.Start && p < image.Value.End;
        }

        private static (IntPtr Start, IntPtr End)? MeasureAdlxImage()
        {
            try
            {
                IntPtr baseAddr = GetModuleHandleW(Dll);
                if (baseAddr == IntPtr.Zero) return null;

                int lfanew = Marshal.ReadInt32(baseAddr, 0x3C);
                if (lfanew <= 0 || lfanew > 0x1000) return null;

                // NT headers: signature (4) + file header (20) -> optional header.
                // SizeOfImage sits at optional-header offset 0x38 (PE32 and PE32+).
                int sizeOfImage = Marshal.ReadInt32(baseAddr + lfanew + 4 + 20, 0x38);
                if (sizeOfImage <= 0 || sizeOfImage > 0x40000000) return null;

                return (baseAddr, baseAddr + sizeOfImage);
            }
            catch
            {
                return null;
            }
        }
    }
}
