using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Native
{
    internal static partial class NativeMethods
    {
        internal static class Etw
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct WnodeHeader
            {
                public uint BufferSize;
                public uint ProviderId;
                public ulong HistoricalContext;
                public ulong TimeStamp;
                public Guid Guid;
                public uint ClientContext;
                public uint Flags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct EVENT_TRACE_PROPERTIES
            {
                public WnodeHeader Wnode;
                public uint BufferSize;
                public uint MinimumBuffers;
                public uint MaximumBuffers;
                public uint MaximumFileSize;
                public uint LogFileMode;
                public uint FlushTimer;
                public uint EnableFlags;
                public int AgeLimit;
                public uint NumberOfBuffers;
                public uint FreeBuffers;
                public uint EventsLost;
                public uint BuffersWritten;
                public uint LogBuffersLost;
                public uint RealTimeBuffersLost;
                public IntPtr LoggerThreadId;
                public uint LogFileNameOffset;
                public uint LoggerNameOffset;
            }

            public const uint EVENT_TRACE_CONTROL_QUERY = 0;
            public const uint EVENT_TRACE_CONTROL_STOP = 1;
            public const uint EVENT_TRACE_CONTROL_UPDATE = 2;
            public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
            
            public const int ERROR_SUCCESS = 0;
            public const int ERROR_MORE_DATA = 234;

            [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
            public static extern int QueryAllTracesW(
                [Out] IntPtr[] propertyArray,
                int propertyArrayCount,
                out int sessionCount);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern int ControlTraceW(
                ulong traceHandle,
                string instanceName,
                IntPtr properties,
                uint controlCode);
        }
    }
}
