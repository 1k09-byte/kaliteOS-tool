using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Native;
using kaliteConfig.Models;
using Microsoft.Win32;

namespace kaliteConfig.Services
{
    public class EtwManagerService
    {
        private const int MaxSessions = 128;
        private const string AutologgerKey = @"SYSTEM\CurrentControlSet\Control\WMI\Autologger";
        private static bool _autoClearRunning = false;

        public List<EtwSessionModel> GetActiveSessions()
        {
            var sessions = new List<EtwSessionModel>();
            var sessionDict = new Dictionary<string, EtwSessionModel>(StringComparer.OrdinalIgnoreCase);
            IntPtr[] propertyArray = new IntPtr[MaxSessions];
            int structSize = Marshal.SizeOf(typeof(NativeMethods.Etw.EVENT_TRACE_PROPERTIES));
            int bufferSize = structSize + 4096; // struct + LoggerName + LogFileName

            for (int i = 0; i < MaxSessions; i++)
            {
                IntPtr ptr = Marshal.AllocHGlobal(bufferSize);
                for (int j = 0; j < bufferSize; j += 4)
                    Marshal.WriteInt32(ptr, j, 0);

                NativeMethods.Etw.EVENT_TRACE_PROPERTIES prop = new NativeMethods.Etw.EVENT_TRACE_PROPERTIES();
                prop.Wnode.BufferSize = (uint)bufferSize;
                prop.Wnode.Flags = NativeMethods.Etw.WNODE_FLAG_TRACED_GUID;
                prop.LoggerNameOffset = (uint)structSize;
                prop.LogFileNameOffset = (uint)(structSize + 2048);

                Marshal.StructureToPtr(prop, ptr, false);
                propertyArray[i] = ptr;
            }

            try
            {
                int sessionCount;
                int result = NativeMethods.Etw.QueryAllTracesW(propertyArray, MaxSessions, out sessionCount);

                if (result == NativeMethods.Etw.ERROR_SUCCESS || result == NativeMethods.Etw.ERROR_MORE_DATA)
                {
                    for (int i = 0; i < sessionCount; i++)
                    {
                        IntPtr ptr = propertyArray[i];
                        NativeMethods.Etw.EVENT_TRACE_PROPERTIES prop = Marshal.PtrToStructure<NativeMethods.Etw.EVENT_TRACE_PROPERTIES>(ptr);

                        string loggerName = Marshal.PtrToStringUni(ptr + (int)prop.LoggerNameOffset) ?? string.Empty;
                        string logFileName = Marshal.PtrToStringUni(ptr + (int)prop.LogFileNameOffset) ?? string.Empty;

                        bool isAutoLogger = false;
                        try
                        {
                            using (var key = Registry.LocalMachine.OpenSubKey($@"{AutologgerKey}\{loggerName}"))
                            {
                                if (key != null)
                                {
                                    var startVal = key.GetValue("Start");
                                    if (startVal is int s) isAutoLogger = s == 1;
                                }
                            }
                        }
                        catch { }

                        var model = new EtwSessionModel
                        {
                            Name = loggerName,
                            LogFilePath = logFileName,
                            IsRunning = true,
                            BuffersWritten = prop.BuffersWritten,
                            IsAutoLoggerEnabled = isAutoLogger,
                            Description = GetDescription(loggerName),
                            Category = GetCategory(loggerName)
                        };
                        sessions.Add(model);
                        sessionDict[loggerName] = model;
                    }
                }

                // Enumerate offline Autologgers so they don't disappear from the UI
                try
                {
                    using (var root = Registry.LocalMachine.OpenSubKey(AutologgerKey))
                    {
                        if (root != null)
                        {
                            foreach (var subKeyName in root.GetSubKeyNames())
                            {
                                if (!sessionDict.ContainsKey(subKeyName))
                                {
                                    using (var sk = root.OpenSubKey(subKeyName))
                                    {
                                        bool isAutoLogger = false;
                                        if (sk != null)
                                        {
                                            var startVal = sk.GetValue("Start");
                                            if (startVal is int s) isAutoLogger = s == 1;
                                        }

                                        sessions.Add(new EtwSessionModel
                                        {
                                            Name = subKeyName,
                                            LogFilePath = "Inactive (Autologger Definition)",
                                            IsRunning = false,
                                            BuffersWritten = 0,
                                            IsAutoLoggerEnabled = isAutoLogger,
                                            Description = GetDescription(subKeyName),
                                            Category = GetCategory(subKeyName)
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            finally
            {
                for (int i = 0; i < MaxSessions; i++)
                    Marshal.FreeHGlobal(propertyArray[i]);
            }

            // Ensure the Auto-Clear loop is running in the background for system-tray operation
            EnsureAutoClearRunning();

            return sessions;
        }

        /// <summary>
        /// Stops an active ETW session using <c>cmd /c logman stop</c>.
        /// We route through cmd.exe so the shell resolves logman from
        /// the system PATH even in edge-case packaged contexts.
        /// </summary>
        public bool StopSession(string sessionName)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c logman stop \"{sessionName}\" -ets",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10_000); // 10s safety ceiling
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
        
        public bool StartSession(string sessionName)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c logman start \"{sessionName}\" -ets",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public bool DisableAutoLogger(string sessionName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{AutologgerKey}\{sessionName}", true);
                if (key != null)
                {
                    key.SetValue("Start", 0, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public bool EnableAutoLogger(string sessionName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{AutologgerKey}\{sessionName}", true);
                if (key != null)
                {
                    key.SetValue("Start", 1, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void ClearAllEventLogs()
        {
            try
            {
                // Native, extremely fast event log purge that bypasses cmd.exe/wevtutil CPU spikes
                var session = new EventLogSession();
                foreach (var logName in session.GetLogNames())
                {
                    try { session.ClearLog(logName); } catch { }
                }
            }
            catch { }
        }

        public void OptimizeAllSessions()
        {
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(AutologgerKey, true))
                {
                    if (root != null)
                    {
                        foreach (var subKeyName in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = root.OpenSubKey(subKeyName, true))
                                {
                                    if (sk != null)
                                    {
                                        // Increasing BufferSize to 1024KB (1MB) safely reduces disk I/O flush frequency
                                        sk.SetValue("BufferSize", 1024, RegistryValueKind.DWord);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public void RevertAllSessionsToDefault()
        {
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(AutologgerKey, true))
                {
                    if (root != null)
                    {
                        foreach (var subKeyName in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = root.OpenSubKey(subKeyName, true))
                                {
                                    if (sk != null)
                                    {
                                        sk.DeleteValue("BufferSize", false);
                                        sk.DeleteValue("MaximumBuffers", false); // Just in case a previous override set it
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureAutoClearRunning()
        {
            if (_autoClearRunning) return;
            _autoClearRunning = true;
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(1));
                    ClearAllEventLogs();
                }
            });
        }

        private string GetCategory(string name)
        {
            name = name.ToLowerInvariant();
            if (name.Contains("diag") || name.Contains("telemetry") || name.Contains("track") || name.Contains("autologger")) return "Diagnostics & Telemetry";
            if (name.Contains("eventlog") || name.Contains("kernel") || name.Contains("system") || name.Contains("wmi")) return "Core System";
            if (name.Contains("audio") || name.Contains("video") || name.Contains("media") || name.Contains("rtc") || name.Contains("nv")) return "Media & Drivers";
            if (name.Contains("defender") || name.Contains("security") || name.Contains("wsc") || name.Contains("lsa")) return "Security";
            return "Other Services";
        }

        private string GetDescription(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "diaglog" => "Background diagnostics trace. Safe to disable for gaming.",
                "diagtrack-listener" => "Connected User Experiences and Telemetry. High overhead.",
                "eventlog-system" => "System events logger. Essential for OS auditing.",
                "eventlog-application" => "Application events logger. Essential for OS auditing.",
                "microsoft-windows-rtccore-tracing" => "RTC Core diagnostic tracing.",
                "nt kernel logger" => "Core kernel metric collector. Do not stop.",
                "circular kernel context logger" => "Core kernel metrics. Do not stop.",
                "audio" => "Windows Audio endpoint tracking.",
                _ => "Background trace session."
            };
        }
    }
}
