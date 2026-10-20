using System;

namespace kaliteConfig.GpuOverclock.Models
{
    public sealed class GameProfileBinding
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string ExecutableName { get; set; } = "";

        public string? FullPath { get; set; }

        public Guid ProfileId { get; set; }

        public bool Enabled { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string NormalizedExecutableName
        {
            get
            {
                var n = (ExecutableName ?? "").Trim();
                if (n.Length == 0) return "";
                try
                {
                    var file = System.IO.Path.GetFileName(n);
                    if (!string.IsNullOrEmpty(file)) n = file;
                }
                catch { }
                if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
                return n;
            }
        }

        public bool Matches(string processName, string? processPath)
        {
            var want = NormalizedExecutableName;
            if (want.Length == 0) return false;
            var have = (processName ?? "").Trim();
            if (have.Length == 0) return false;
            if (!have.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) have += ".exe";
            if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(FullPath))
            {
                if (string.IsNullOrWhiteSpace(processPath)) return false;
                var a = Environment.ExpandEnvironmentVariables(FullPath!.Trim()).TrimEnd('\\');
                var b = processPath!.Trim().TrimEnd('\\');
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
    }
}
