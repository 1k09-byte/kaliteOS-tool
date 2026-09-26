// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using kaliteConfig.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    public interface IInstallerService
    {
        bool IsBrowserInstalled(BrowserInstallItem item);
        Task InstallBrowserAsync(BrowserInstallItem item, IProgress<BrowserInstallStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct);
        Task UninstallBrowserAsync(BrowserInstallItem item, CancellationToken ct);
    }
}
