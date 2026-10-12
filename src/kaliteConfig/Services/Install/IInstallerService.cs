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
