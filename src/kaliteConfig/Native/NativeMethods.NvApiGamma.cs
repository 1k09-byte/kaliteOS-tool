// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

/// <summary>
/// The two NvAPI entry points behind the NVIDIA driver's own desktop colour controls.
///
/// These are not in the public NvAPI SDK, and NvAPIWrapper does not surface them, so they
/// are reached through the same single dispatcher entry the wrapper itself uses
/// (<c>nvapi_QueryInterface</c>). The alternative — talking to the monitor over DDC/CI —
/// needs a physical-monitor handle that Windows only hands out in an interactive desktop
/// session, so the driver route is the one that works everywhere the driver does.
///
/// Both functions are called only after <see cref="NvApiSession"/> has initialised NvAPI,
/// because the dispatcher pointer is meaningless until nvapi64.dll is loaded.
/// </summary>
internal static partial class NativeMethods
{
    internal static class NvApiGamma
    {
        private const string NvApi64 = "nvapi64.dll";

        // NvAPI function ids, resolved through the dispatcher rather than exported.
        private const uint FN_SET_TARGET_GAMMA_CORRECTION = 0x7082A053;
        private const uint FN_GET_LUID_FROM_DISPLAY_ID = 0xD4A859F2;

        /// <summary>Entries in one channel of the extended gamma ramp.</summary>
        internal const int RampEntries = 1024;

        private const int RampFloats = RampEntries * 3;

        /// <summary>
        /// <c>MAKE_NVAPI_VERSION(NV_GAMMA_CORRECTION_EX, 1)</c>, which the header defines as
        /// <c>sizeof(struct) | (1 &lt;&lt; 16)</c>. The struct is 4 + 3072*4 + 4 = 12296 bytes.
        /// </summary>
        internal const uint GammaCorrectionExVersion = (4 + RampFloats * 4 + 4) | (1u << 16);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetTargetGammaCorrection(uint displayId, ref NvGammaCorrectionEx ramp);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetLuidFromDisplayId(uint displayId, uint value, out Guid luid);

        /// <summary>
        /// The driver's extended gamma ramp.
        ///
        /// The field order matters and is not what the type name suggests: the ramp sits
        /// <em>between</em> the version and the trailing unknown field, so putting
        /// <see cref="Unknown"/> first writes the driver a table offset by four bytes and it
        /// is silently discarded.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct NvGammaCorrectionEx
        {
            public uint Version;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = RampFloats)]
            public float[] GammaRampEx;

            public uint Unknown;
        }

        [DllImport(NvApi64, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
        private static extern IntPtr QueryInterface64(uint functionId);

        [DllImport("nvapi.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvapi_QueryInterface")]
        private static extern IntPtr QueryInterface32(uint functionId);

        private static SetTargetGammaCorrection? _setGamma;
        private static GetLuidFromDisplayId? _getLuid;
        private static bool _resolved;

        /// <summary>
        /// Why resolution failed, or empty when it succeeded.
        ///
        /// The dispatcher is reached through the one symbol nvapi64.dll actually exports
        /// (plus nvapi_Direct_GetMethod), and it is spelled in lower case on both
        /// architectures. A previous attempt asked for "NvAPI64_QueryInterface", which does
        /// not exist, and the resulting EntryPointNotFoundException was swallowed into a
        /// bare "unsupported" — so the reason is kept instead of being discarded.
        /// </summary>
        public static string UnavailableReason { get; private set; } = "";

        private static IntPtr QueryInterface(uint functionId)
            => IntPtr.Size == 8 ? QueryInterface64(functionId) : QueryInterface32(functionId);

        /// <summary>
        /// Resolves both entry points once. Returns false when the dispatcher refuses,
        /// which is what an older driver without the desktop-colour API looks like.
        /// </summary>
        private static bool EnsureResolved()
        {
            if (_resolved) return _setGamma is not null && _getLuid is not null;

            _resolved = true;
            try
            {
                IntPtr setGamma = QueryInterface(FN_SET_TARGET_GAMMA_CORRECTION);
                IntPtr getLuid = QueryInterface(FN_GET_LUID_FROM_DISPLAY_ID);
                if (setGamma == IntPtr.Zero || getLuid == IntPtr.Zero)
                {
                    UnavailableReason =
                        $"the dispatcher returned no entry point (gamma=0x{setGamma.ToInt64():X}, " +
                        $"luid=0x{getLuid.ToInt64():X}); this driver does not expose the desktop colour API";
                    return false;
                }

                _setGamma = Marshal.GetDelegateForFunctionPointer<SetTargetGammaCorrection>(setGamma);
                _getLuid = Marshal.GetDelegateForFunctionPointer<GetLuidFromDisplayId>(getLuid);
                UnavailableReason = "";
                return true;
            }
            catch (DllNotFoundException ex)
            {
                UnavailableReason = $"the NvAPI library could not be loaded ({ex.Message})";
            }
            catch (EntryPointNotFoundException)
            {
                UnavailableReason = "nvapi's dispatcher entry point is missing from this driver";
            }
            catch (Exception ex)
            {
                UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
            }

            _setGamma = null;
            _getLuid = null;
            return false;
        }

        /// <summary>True when this driver exposes the desktop-colour controls.</summary>
        public static bool IsAvailable => EnsureResolved();

        /// <summary>
        /// The driver's 32-bit LUID for an NvAPI display id.
        ///
        /// The API answers with a GUID and the part the registry keys are named after is its
        /// second dword, obfuscated by the driver. Returns 0 when the display cannot be
        /// reached, which is also the "no colours recorded" case.
        /// </summary>
        public static uint GetLuid(uint displayId)
        {
            LastLuidStatus = 0;
            if (!EnsureResolved()) { LastLuidStatus = int.MinValue; return 0; }
            try
            {
                int status = _getLuid!(displayId, 1, out Guid guid);
                LastLuidStatus = status;
                if (status != 0) return 0;
                return BitConverter.ToUInt32(guid.ToByteArray(), 4) ^ 0xF0000000u;
            }
            catch (Exception)
            {
                LastLuidStatus = int.MinValue;
                return 0;
            }
        }

        /// <summary>
        /// The NvAPI status from the most recent <see cref="GetLuid"/> call. Kept because a
        /// bare "no LUID" says nothing about why: NV_STATUS codes name the reason
        /// (invalid parameter, not supported, no permission) and that is the difference
        /// between a driver we must talk to differently and a display it cannot reach.
        /// 0 means the call succeeded; int.MinValue means it never ran.
        /// </summary>
        public static int LastLuidStatus { get; private set; }

        /// <summary>Pushes a fully built ramp to one display. True when the driver took it.</summary>
        public static bool SetGammaCorrection(uint displayId, float[] ramp)
        {
            if (!EnsureResolved()) return false;
            if (ramp is null || ramp.Length != RampFloats) return false;

            try
            {
                var packet = new NvGammaCorrectionEx
                {
                    Version = GammaCorrectionExVersion,
                    GammaRampEx = ramp,
                    Unknown = 1,
                };
                return _setGamma!(displayId, ref packet) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
