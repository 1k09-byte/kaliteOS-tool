using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// Appends every AppliedChangeLogEntry to a human-readable local log, keeps
    /// an in-memory tail for the in-app viewer, and exposes the log path so the
    /// user can open the raw file. Both a debugging tool and a transparency
    /// tool ("what did this app change on my system, and when").
    /// </summary>
    public sealed class OverclockChangeLogger
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly List<AppliedChangeLogEntry> _tail = new();
        private const int TailLimit = 500;

        public OverclockChangeLogger(string? directory = null)
        {
            var dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "gpu-overclock-changes.log");
        }

        /// <summary>Absolute path of the human-readable log file.</summary>
        public string LogFilePath => _path;

        public void Log(AppliedChangeLogEntry entry)
        {
            lock (_gate)
            {
                _tail.Add(entry);
                if (_tail.Count > TailLimit) _tail.RemoveRange(0, _tail.Count - TailLimit);
                try
                {
                    var sb = new StringBuilder();
                    sb.Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                    sb.Append(entry.Result).Append("  ");
                    sb.Append(entry.ControlName).Append(": ").Append(entry.OldValue);
                    if (!string.Equals(entry.OldValue, entry.NewValue, StringComparison.Ordinal))
                        sb.Append(" -> ").Append(entry.NewValue);
                    sb.Append("  (").Append(entry.Source).Append(')');
                    if (!string.IsNullOrEmpty(entry.FailureReason))
                        sb.Append("  — ").Append(entry.FailureReason);
                    sb.AppendLine();
                    File.AppendAllText(_path, sb.ToString());
                }
                catch
                {
                    // Logging must never take the module down.
                }
            }
        }

        /// <summary>Recent entries for the in-app viewer (newest last).</summary>
        public IReadOnlyList<AppliedChangeLogEntry> Tail()
        {
            lock (_gate) return _tail.ToArray();
        }
    }
}
