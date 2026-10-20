using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
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
                        sb.Append("  - ").Append(entry.FailureReason);
                    sb.AppendLine();
                    File.AppendAllText(_path, sb.ToString());
                }
                catch
                {
                }
            }
        }

        public IReadOnlyList<AppliedChangeLogEntry> Tail()
        {
            lock (_gate) return _tail.ToArray();
        }
    }
}
