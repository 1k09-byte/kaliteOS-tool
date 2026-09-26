// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Services;

namespace kaliteConfig.Services;

public interface IWindhawkInstallerService
{
    Task<(string Version, string DownloadUrl)> ResolveLatestOfflineInstallerAsync(CancellationToken ct = default);
    Task<string> DownloadInstallerAsync(string downloadUrl, IProgress<string>? status = null, CancellationToken ct = default);
    Task InstallSilentlyAsync(string installerPath, IProgress<string>? status = null, CancellationToken ct = default);
    Task<WindhawkInstallerService.WindhawkInstallerResult> ImportSettingsAsync(
        string jsonPath, IProgress<string>? status = null, CancellationToken ct = default);
    Task<WindhawkInstallerService.ModUpdateResult> UpdateModsIfAvailableAsync(
        string cliPath, string workingDirectory, IProgress<string>? status = null, CancellationToken ct = default);
    void CleanupTempFiles();
    static void CleanupFile(string path) => WindhawkInstallerService.CleanupFile(path);
}
