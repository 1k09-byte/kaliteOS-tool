using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services
{
    public interface IInstallerService
    {
        bool IsBrowserInstalled(BrowserInstallItem item);
        Task InstallBrowserAsync(BrowserInstallItem item, IProgress<BrowserInstallStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct);
        Task UpdateBrowserAsync(BrowserInstallItem item, IProgress<BrowserInstallStatus> progress, IProgress<double> downloadProgress, IProgress<string> errorProgress, CancellationToken ct);
        Task UninstallBrowserAsync(BrowserInstallItem item, CancellationToken ct);
        void DetectUpdate(BrowserInstallItem item);
        Task<HashSet<string>> GetUpgradableWingetIdsAsync(CancellationToken ct);
        string? ApplyExtensions(BrowserInstallItem item);
    }
}
