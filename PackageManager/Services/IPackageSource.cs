using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>Discover search modes (mirrors the reference Search panel).</summary>
public enum PackageSearchMode
{
    Name,
    Id,
    Both,
    Exact,
    Similar,
}

/// <summary>Progress for one running backend operation.</summary>
public sealed class PackageOperationProgress
{
    /// <summary>0-100 when measurable, else null.</summary>
    public double? Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Outcome of one backend operation (per-item results never abort a batch).</summary>
public sealed class PackageOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static PackageOperationResult Ok(string message = "") => new() { Success = true, Message = message };
    public static PackageOperationResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// Every package-manager backend implements this. A source that cannot do an
/// operation advertises it via the Can* flags (checked by the UI) and its
/// method returns a failed result - never a throw for expected cases.
/// </summary>
public interface IPackageSource
{
    string SourceId { get; }
    string DisplayName { get; }
    string Description { get; }

    bool CanSearch { get; }
    bool CanInstall { get; }
    bool CanUpdate { get; }
    bool CanUninstall { get; }
    bool CanListInstalled { get; }
    bool CanListUpdates { get; }

    /// <summary>Probe the underlying tool (exe present? API reachable?). Never throws.</summary>
    Task<PackageSourceStatus> DetectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, PackageSearchMode mode, CancellationToken ct = default);

    Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PackageInfo>> GetAvailableUpdatesAsync(CancellationToken ct = default);

    Task<PackageOperationResult> InstallAsync(
        PackageInfo package, string? scope,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct);

    Task<PackageOperationResult> UpdateAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct);

    Task<PackageOperationResult> UninstallAsync(
        PackageInfo package,
        IProgress<PackageOperationProgress>? progress, CancellationToken ct);
}
