using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native;

internal static partial class NativeMethods
{
    // Configuration Manager (cfgmgr32): lets us ask whether a device instance
    // is actually present in the devnode tree. Registry Enum keys linger after
    // hardware is removed/re-seated, so presence is the only reliable signal.
    internal static partial class CfgMgr32
    {
        internal const uint CR_SUCCESS = 0x00000000;
        internal const uint CR_NO_SUCH_DEVNODE = 0x0000000D;
        internal const uint CR_INVALID_DEVNODE = 0x00000011;

        // DN_* devnode status bits (cfg.h)
        internal const uint DN_HAS_PROBLEM = 0x00000400;

        [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [LibraryImport("cfgmgr32.dll")]
        internal static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        /// <summary>
        /// True when the device instance is present and problem-free (or present
        /// at all, per <paramref name="requireNoProblem"/>). Non-present
        /// ("phantom") entries — GPUs removed, disabled iGPUs, stale reinstalls —
        /// return false.
        /// </summary>
        internal static bool IsDevicePresent(string deviceId, bool requireNoProblem = true)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            int cr = CM_Locate_DevNodeW(out uint devInst, deviceId, 0);
            if (cr != CR_SUCCESS)
                return false; // CR_NO_SUCH_DEVNODE / CR_INVALID_DEVNODE → not in the live tree
            cr = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            if (cr != CR_SUCCESS) return false;
            return requireNoProblem ? (status & DN_HAS_PROBLEM) == 0 : true;
        }
    }
}
