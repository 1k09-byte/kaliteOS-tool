using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// Holds every backend. WinGet is the only implemented one so far; every
/// other row is an honest not-implemented placeholder (never a fake that
/// pretends to work) until its backend lands, one at a time.
/// </summary>
public sealed class PackageSourceRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly List<IPackageSource> _sources = new();
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _exeOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;

    public PackageSourceRegistry(string? stateDirectory = null)
    {
        string dir = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "package-manager");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "sources.json");
        LoadState();

        Register(new WinGetPackageSource());
        Register(new NotImplementedPackageSource(PackageSourceIds.Scoop, "Scoop", "Portable user-space packages (CLI parsing when built)."));
        Register(new NotImplementedPackageSource(PackageSourceIds.Chocolatey, "Chocolatey", "Community Windows packages (library or CLI when built)."));
        Register(new NotImplementedPackageSource(PackageSourceIds.Npm, "npm", "Global Node.js packages."));
        Register(new NotImplementedPackageSource(PackageSourceIds.Pip, "pip", "Global Python packages (no registry search API)."));
        Register(new NotImplementedPackageSource(PackageSourceIds.Cargo, "Cargo", "Rust binaries via crates.io."));
        Register(new NotImplementedPackageSource(PackageSourceIds.Vcpkg, "vcpkg", "C/C++ libraries."));
        Register(new NotImplementedPackageSource(PackageSourceIds.DotNetTool, ".NET Tool", "Global .NET tools."));
        Register(new NotImplementedPackageSource(PackageSourceIds.PowerShell7, "PowerShell Gallery (7.x)", "PS modules for pwsh.exe.", "pwsh.exe"));
        Register(new NotImplementedPackageSource(PackageSourceIds.PowerShell5, "PowerShell Gallery (5.x)", "PS modules for powershell.exe.", "powershell.exe"));
        Register(new NotImplementedPackageSource(PackageSourceIds.Local, "Local PC", "Installed Win32 + Store apps (installed-list only when built)."));
    }

    public IReadOnlyList<IPackageSource> Sources => _sources;

    public void Register(IPackageSource source) => _sources.Add(source);

    public IPackageSource? Find(string sourceId) =>
        _sources.FirstOrDefault(s => s.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(IPackageSource source)
    {
        if (_enabled.TryGetValue(source.SourceId, out bool enabled)) return enabled;
        return true;
    }

    public void SetEnabled(string sourceId, bool enabled)
    {
        _enabled[sourceId] = enabled;
        SaveState();
        if (Find(sourceId) is WinGetPackageSource winget && _exeOverrides.TryGetValue(sourceId, out string? exe))
            winget.ExecutableOverride = exe;
    }

    public string GetExecutableOverride(string sourceId) =>
        _exeOverrides.TryGetValue(sourceId, out string? exe) ? exe : "";

    public void SetExecutableOverride(string sourceId, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) _exeOverrides.Remove(sourceId);
        else _exeOverrides[sourceId] = executablePath.Trim();
        SaveState();
        if (Find(sourceId) is WinGetPackageSource winget)
            winget.ExecutableOverride = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath.Trim();
    }

    /// <summary>Probes every backend (WinGet real; placeholders report not-implemented).</summary>
    public async Task<IReadOnlyList<PackageSourceStatus>> DetectAsync(CancellationToken ct = default)
    {
        var list = new List<PackageSourceStatus>();
        foreach (var source in _sources)
        {
            var status = await source.DetectAsync(ct).ConfigureAwait(false);
            status.IsEnabled = IsEnabled(source);
            if (source is WinGetPackageSource winget)
                winget.ExecutableOverride = string.IsNullOrWhiteSpace(GetExecutableOverride(source.SourceId))
                    ? null : GetExecutableOverride(source.SourceId);
            list.Add(status);
        }
        return list;
    }

    /// <summary>Sources usable for a given view (implemented + enabled).</summary>
    public IReadOnlyList<IPackageSource> ActiveSources() =>
        _sources.Where(s => IsEnabled(s) && s is not NotImplementedPackageSource).ToList();

    private sealed record PersistedState(Dictionary<string, bool>? Enabled, Dictionary<string, string>? ExeOverrides);

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath), JsonOpts);
            if (state?.Enabled != null)
                foreach (var kv in state.Enabled) _enabled[kv.Key] = kv.Value;
            if (state?.ExeOverrides != null)
                foreach (var kv in state.ExeOverrides) _exeOverrides[kv.Key] = kv.Value;
        }
        catch { /* corrupt state starts fresh */ }
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(
                new PersistedState(
                    new Dictionary<string, bool>(_enabled, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(_exeOverrides, StringComparer.OrdinalIgnoreCase)),
                JsonOpts));
        }
        catch { }
    }

    /// <summary>
    /// Honest placeholder: advertises no capabilities and reports
    /// not-implemented, so the preferences page shows a real "Not
    /// implemented yet" row instead of a fake backend.
    /// </summary>
    private sealed class NotImplementedPackageSource : IPackageSource
    {
        private readonly string? _hintExe;

        public NotImplementedPackageSource(string id, string name, string description, string? hintExe = null)
        {
            SourceId = id;
            DisplayName = name;
            Description = description;
            _hintExe = hintExe;
        }

        public string SourceId { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public bool CanSearch => false;
        public bool CanInstall => false;
        public bool CanUpdate => false;
        public bool CanUninstall => false;
        public bool CanListInstalled => false;
        public bool CanListUpdates => false;

        public Task<PackageSourceStatus> DetectAsync(CancellationToken ct = default)
        {
            // Presence probe only (informational overture for the real
            // backend): is the tool even on PATH? Still not implemented.
            bool present = false;
            string version = "";
            if (_hintExe != null)
            {
                try
                {
                    using var proc = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = _hintExe,
                            Arguments = "--version",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        },
                    };
                    if (proc.Start())
                    {
                        proc.WaitForExit(8000);
                        if (proc.HasExited && proc.ExitCode == 0)
                        {
                            present = true;
                            version = (proc.StandardOutput.ReadToEnd() + " " + proc.StandardError.ReadToEnd())
                                .Trim().Split('\n')[0].Trim();
                            if (version.Length > 64) version = version.Substring(0, 64);
                        }
                        else if (!proc.HasExited)
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }
                catch { }
            }
            return Task.FromResult(new PackageSourceStatus
            {
                SourceId = SourceId,
                DisplayName = DisplayName,
                Description = present
                    ? Description + $" (tool detected{(version.Length > 0 ? $", {version}" : "")} — backend not built yet)"
                    : Description,
                IsImplemented = false,
                IsEnabled = false,
                IsDetected = present,
                DetectedVersion = version,
            });
        }

        private static PackageOperationResult Unsupported() =>
            PackageOperationResult.Fail("This package source is not implemented yet.");

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, PackageSearchMode mode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageInfo>>(Array.Empty<PackageInfo>());

        public Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageInfo>>(Array.Empty<PackageInfo>());

        public Task<IReadOnlyList<PackageInfo>> GetAvailableUpdatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageInfo>>(Array.Empty<PackageInfo>());

        public Task<PackageOperationResult> InstallAsync(PackageInfo package, string? scope, IProgress<PackageOperationProgress>? progress, CancellationToken ct)
            => Task.FromResult(Unsupported());

        public Task<PackageOperationResult> UpdateAsync(PackageInfo package, IProgress<PackageOperationProgress>? progress, CancellationToken ct)
            => Task.FromResult(Unsupported());

        public Task<PackageOperationResult> UninstallAsync(PackageInfo package, IProgress<PackageOperationProgress>? progress, CancellationToken ct)
            => Task.FromResult(Unsupported());
    }
}
