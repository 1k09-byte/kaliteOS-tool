using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

public enum PackageSearchMode
{
    Name,
    Id,
    Both,
    Exact,
    Similar,
}

public sealed class PackageOperationProgress
{
    public double? Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class PackageOperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static PackageOperationResult Ok(string message = "") => new() { Success = true, Message = message };
    public static PackageOperationResult Fail(string message) => new() { Success = false, Message = message };
}

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
