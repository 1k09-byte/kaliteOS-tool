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
        public bool AnyControlSupported => ManualGfx || ManualVram || ManualFan || ManualPower;
    }

    internal static partial class AmdAdlxInterop
    {
        private const string Dll = "amdadlx64.dll";

        private const ulong RequestedVersion = (2UL << 48) | (0UL << 32) | (0UL << 16) | 125UL;

        [DllImport(Dll, EntryPoint = "ADLXInitialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxInitialize(ulong version, out IntPtr ppSystem);

        [DllImport(Dll, EntryPoint = "ADLXTerminate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxTerminate();

        [DllImport(Dll, EntryPoint = "ADLXQueryVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern int AdlxQueryVersion(out IntPtr version);

        private const int SystemGetGpus = 1;
        private const int SystemGetGpuTuningServices = 8;

        private const int ListSize = 3;
        private const int ListAtGpu = 11;

        private const int GpuType = 5;
        private const int GpuIsExternal = 6;
        private const int GpuName = 7;
        private const int GpuPnpString = 9;
        private const int GpuTotalVram = 11;

        private const int TuningIsSupportedManualGfx = 8;
        private const int TuningIsSupportedManualVram = 9;
        private const int TuningIsSupportedManualFan = 10;
        private const int TuningIsSupportedManualPower = 11;

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

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long AcquireFn(IntPtr self);

        private const int IfaceAcquire = 0;

        private static readonly object Gate = new();
        private static bool _initialized;
        private static IntPtr _system;

        public static bool IsAvailable
        {
            get { lock (Gate) { EnsureInitialized(); return _initialized; } }
        }

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
            if (_system != IntPtr.Zero) return false;

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
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (BadImageFormatException)
            {
            }

            return _initialized;
        }

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

                            Fn<AcquireFn>(gpu, IfaceAcquire)(gpu);
                            sessions.Add(new AmdGpuTuningSession(gpu, tuning, support));
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                catch (Exception)
                {
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
            uint vramMb = 0;
            try
            {
                if (Fn<GpuVramFn>(gpu, GpuTotalVram)(gpu, out uint adlxMb) == 0)
                    vramMb = adlxMb;
            }
            catch { vramMb = 0; }
            if (vramMb == 0)
                vramMb = ReadRegistryVramMb(pnp);

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

        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static uint ReadRegistryVramMb(string pnpId)
        {
            try
            {
                if (string.IsNullOrEmpty(pnpId)) return 0;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    DisplayClassKey, writable: false);
                if (key is null) return 0;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is null) continue;
                    if (child.GetValue("MatchingDeviceId")?.ToString() is not string matching ||
                        !MatchesPnpId(matching, pnpId))
                        continue;
                    object? raw = child.GetValue("HardwareInformation.qwMemorySize")
                        ?? child.GetValue("HardwareInformation.MemorySize");
                    long bytes = raw switch
                    {
                        int i => i,
                        uint u => u,
                        long l => l,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                        string s when long.TryParse(s, out long v) => v,
                        _ => 0,
                    };
                    if (bytes > 0) return (uint)(bytes / 1024 / 1024);
                }
            }
            catch { }
            return 0;
        }

        private static bool MatchesPnpId(string matching, string pnpId)
        {
            if (string.IsNullOrEmpty(matching) || string.IsNullOrEmpty(pnpId)) return false;
            if (pnpId.StartsWith(matching, StringComparison.OrdinalIgnoreCase)) return true;
            if (matching.StartsWith(pnpId, StringComparison.OrdinalIgnoreCase)) return true;
            static string Core(string s)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    s, @"ven_[0-9a-f]{4}[&_]dev_[0-9a-f]{4}",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Value.ToLowerInvariant() : s.Replace('\\', '_').ToLowerInvariant();
            }
            return string.Equals(Core(matching), Core(pnpId), StringComparison.Ordinal);
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

            if (!PointsIntoAdlx(slot))
                throw new EntryPointNotFoundException(
                    $"ADLX vtable slot {index} points outside {Dll} - refusing to call it.");

            return Marshal.GetDelegateForFunctionPointer<T>(slot);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        private static (IntPtr Start, IntPtr End)? _adlxImage;

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
