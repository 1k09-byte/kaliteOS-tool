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

namespace kaliteConfig.GpuOverclock.Models
{
    /// <summary>
    /// Binds a saved <see cref="OverclockProfile"/> to a game/app executable
    /// so the profile auto-applies when the process launches and the default
    /// profile returns when it exits. Persisted by ProfileStorageService in
    /// game-bindings.json (separate file - bindings reference profiles by Id,
    /// so renaming a profile never breaks its bindings; deleting one does,
    /// and stale bindings are skipped with a log entry).
    /// </summary>
    public sealed class GameProfileBinding
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Executable file name, e.g. "Valorant.exe". Matched
        /// case-insensitively against running process names. This is the
        /// primary match key - portable across install locations.
        /// </summary>
        public string ExecutableName { get; set; } = "";

        /// <summary>
        /// Optional full-path override to disambiguate generically-named
        /// executables (e.g. two games both shipping "Game.exe"). When set,
        /// the process's full path must match too.
        /// </summary>
        public string? FullPath { get; set; }

        /// <summary>Profile applied while the bound process runs.</summary>
        public Guid ProfileId { get; set; }

        public bool Enabled { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Normalized name used for process matching (no path, with .exe).</summary>
        public string NormalizedExecutableName
        {
            get
            {
                var n = (ExecutableName ?? "").Trim();
                if (n.Length == 0) return "";
                // Tolerate a full path pasted into the name field.
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
