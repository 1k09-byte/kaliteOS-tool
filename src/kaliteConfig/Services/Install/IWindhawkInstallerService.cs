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
