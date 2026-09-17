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
