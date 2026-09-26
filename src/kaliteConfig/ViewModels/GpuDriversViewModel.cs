// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.Controls;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace kaliteConfig.ViewModels
{
    public partial class GpuDriversViewModel : ObservableObject
    {
        private readonly GpuDriverService _gpuService = new();
        private readonly NvidiaDriverService _nvidiaService = new();
        private readonly AmdDriverService _amdService = new();
        private readonly NvidiaPackageService _packageService = new();
        private static readonly HttpClient _http = new();

        private CancellationTokenSource? _cts;

        public ObservableCollection<GpuDriverItem> Drivers { get; } = new();
        public ObservableCollection<DetectedGpu> DetectedGpus { get; } = new();

        // Cards actually shown: only vendors present on this machine.
        // Falls back to all three when nothing has been detected yet.
        public ObservableCollection<GpuDriverItem> VisibleDrivers { get; } = new();

        [ObservableProperty]
        public partial GpuDriverItem? SelectedDriver { get; set; }

        [ObservableProperty]
        public partial bool IsInstalling { get; set; }

        [ObservableProperty]
        public partial string InstallStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsDetecting { get; set; }

        /// <summary>Driver channel for the NVIDIA lookup: Game Ready (default) or Studio.</summary>
        [ObservableProperty]
        public partial bool StudioChannel { get; set; }

        public int StudioChannelIndex
        {
            get => StudioChannel ? 1 : 0;
            set => StudioChannel = (value == 1);
        }
        partial void OnStudioChannelChanged(bool value) => OnPropertyChanged(nameof(StudioChannelIndex));

        /// <summary>
        /// Look up the laptop variant of the GPU ("… Laptop GPU" products).
        /// Auto-enabled at detection time when WMI reports a laptop GPU.
        /// </summary>
        [ObservableProperty]
        public partial bool NotebookGpu { get; set; }

        public int NotebookGpuIndex
        {
            get => NotebookGpu ? 1 : 0;
            set => NotebookGpu = (value == 1);
        }
        partial void OnNotebookGpuChanged(bool value) => OnPropertyChanged(nameof(NotebookGpuIndex));

        [ObservableProperty]
        public partial string DetectStatusText { get; set; } = "Detecting GPUs...";

        public string AdapterCountText => $"{DetectedGpus.Count} adapter{(DetectedGpus.Count == 1 ? "" : "s")}";

        // NVIDIA properties
        public ObservableCollection<NvidiaDriverPackage> NvidiaPackages { get; } = new();
        
        [ObservableProperty]
        public partial NvidiaDriverPackage? SelectedNvidiaPackage { get; set; }

        // AMD properties 
        public ObservableCollection<AmdDriverPackageConfig> AmdPackages { get; } = new();

        public GpuDriversViewModel()
        {
            Drivers.Add(new GpuDriverItem
            {
                Name = "NVIDIA GeForce Game Ready Driver",
                Vendor = "NVIDIA",
                Description = "Game Ready WHQL driver, installed silently (display driver + bundled components from the official package).",
                SilentInstallArgs = "-s -noreboot",
                InstallerFileName = "nvidia_driver.exe",
                VendorPageUrl = "https://www.nvidia.com/en-us/geforce/drivers/"
            });
            Drivers.Add(new GpuDriverItem
            {
                Name = "AMD Software: Adrenalin Edition",
                Vendor = "AMD",
                Description = "Official Adrenalin package. AMD offers no reliable silent flags natively, so the app scrapes the newest executable payload dynamically bypassing CDN protections.",
                DownloadUrl = string.Empty,
                InstallerFileName = "amd_software_installer.exe",
                VendorPageUrl = "https://www.amd.com/en/support/downloads/drivers.html",
                GuidedInstallOnly = true
            });
            Drivers.Add(new GpuDriverItem
            {
                Name = "Intel Arc Graphics Driver",
                Vendor = "Intel",
                Description = "Intel ships versioned bundles only - this card opens the official Intel download center.",
                VendorPageUrl = "https://www.intel.com/content/www/us/en/download-center/home.html",
                GuidedInstallOnly = true,
                IsPageOnly = true
            });

            UpdateVisibleDrivers();
        }

        [RelayCommand]
        private async Task DetectGpusAsync()
        {
            if (IsDetecting) return;
            IsDetecting = true;
            DetectStatusText = "Detecting GPUs...";
            try
            {
                var gpus = await _gpuService.DetectGpusAsync();
                DetectedGpus.Clear();

                // The adapter driving the main display is the primary GPU. Windows
                // lists active adapters; the first present, healthy one wins, with
                // discrete cards preferred over integrated when ambiguous.
                var primary = gpus
                    .OrderByDescending(g => g.GpuType == "Discrete")
                    .FirstOrDefault();
                if (primary is not null)
                {
                    gpus[gpus.IndexOf(primary)] = primary with { IsPrimary = true };
                }

                foreach (var gpu in gpus)
                    DetectedGpus.Add(gpu);

                // Sync each driver card with what's on the machine.
                foreach (var driver in Drivers)
                {
                    var match = gpus.FirstOrDefault(g => g.Vendor.Equals(driver.Vendor, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    bool generic = match.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);

                    driver.HardwareName = match.Name;
                    driver.GpuTypeText = generic ? "Generic Driver" : match.GpuType;
                    driver.DeviceTypeText = generic ? "Software Renderer" : match.DeviceType;
                    driver.VramText = match.VramText;
                    driver.IsPrimary = match.IsPrimary;

                    // Laptop GPUs ship distinct driver packages ("… Laptop GPU"
                    // products in NVIDIA's catalog) - default the notebook lookup
                    // on when WMI reports one, so the first Check is correct.
                    if (driver.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        NotebookGpu = match.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase);

                    driver.InstalledVersion = generic ? string.Empty : match.DriverVersion;

                    // Adapter present but Windows has no driver loaded for it
                    // (ConfigManagerErrorCode 28/31/43 → empty version): show the
                    // card in the NotInstalled state so the action button reads
                    // "Install" and the pipeline can fetch a driver for it.
                    if (!generic && string.IsNullOrEmpty(match.DriverVersion))
                    {
                        driver.Status = GpuDriverStatus.NotInstalled;
                        driver.GpuTypeText = match.GpuType;
                        driver.DeviceTypeText = match.DeviceType;
                    }
                    else if (driver.Status is GpuDriverStatus.NotChecked or GpuDriverStatus.NotInstalled or GpuDriverStatus.UpToDate)
                        driver.Status = generic ? GpuDriverStatus.NotInstalled : GpuDriverStatus.NotChecked;
                }

                DetectStatusText = gpus.Count == 0
                    ? "No display adapters found."
                    : $"{gpus.Count} adapter{(gpus.Count == 1 ? "" : "s")} detected.";
                OnPropertyChanged(nameof(AdapterCountText));

                UpdateVisibleDrivers();
            }
            catch (Exception ex)
            {
                DetectStatusText = $"Detection failed: {ex.Message}";
            }
            finally
            {
                IsDetecting = false;
            }
        }

        private void UpdateVisibleDrivers()
        {
            VisibleDrivers.Clear();
            if (DetectedGpus.Count == 0)
            {
                foreach (var driver in Drivers)
                    VisibleDrivers.Add(driver);
                return;
            }
            var vendorsDetected = DetectedGpus.Select(g => g.Vendor).ToList();
            foreach (var driver in Drivers)
            {
                if (vendorsDetected.Contains(driver.Vendor, StringComparer.OrdinalIgnoreCase))
                {
                    VisibleDrivers.Add(driver);
                }
            }
            if (SelectedDriver == null && VisibleDrivers.Count > 0)
            {
                SelectedDriver = VisibleDrivers.FirstOrDefault();
            }
        }

        [RelayCommand]
        private async Task CheckDriverAsync(GpuDriverItem? item)
        {
            if (item is null || item.IsPageOnly) return;
            
            var gpu = DetectedGpus.FirstOrDefault(g => g.Vendor.Equals(item.Vendor, StringComparison.OrdinalIgnoreCase));
            if (gpu == null) return;
            
            if (item.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                item.Status = GpuDriverStatus.Downloading;
                try
                {
                    // Fetch up to 15 versions for dropdown selection. Prefer
                    // the PCI device ID (hardware identity - works even when
                    // Windows can't name a driverless card); name lookup is the
                    // fallback.
                    var packages = await _nvidiaService.GetDriversByDeviceIdAsync(gpu.PnpDeviceId ?? "", 15, StudioChannel, CancellationToken.None);
                    if (packages.Count == 0)
                        packages = await _nvidiaService.GetDriversAsync(gpu.Name, 15, StudioChannel, NotebookGpu, CancellationToken.None);
                    NvidiaPackages.Clear();
                    foreach(var pkg in packages) NvidiaPackages.Add(pkg);

                    if (NvidiaPackages.Count > 0)
                    {
                        SelectedNvidiaPackage = NvidiaPackages[0];
                        item.LatestVersion = NvidiaPackages[0].Version;
                        item.DownloadUrl = NvidiaPackages[0].DownloadUrl;
                        item.ErrorMessage = string.Empty;

                        // Real comparison: translate the WMI/registry driver-store
                        // version (e.g. 32.0.15.6614) to NVIDIA's format (566.14)
                        // and compare numerically. Registry wins when WMI and the
                        // driver store disagree (registry reflects what actually
                        // loaded; WMI can lag after a pending update).
                        string? effectiveVersion =
                            NvidiaVersionHelper.FromWmiVersion(
                                string.IsNullOrEmpty(gpu.RegistryVersion) ? gpu.DriverVersion : gpu.RegistryVersion)
                            ?? gpu.DriverVersion;

                        if (string.IsNullOrEmpty(effectiveVersion))
                        {
                            item.Status = GpuDriverStatus.NotInstalled;
                        }
                        else
                        {
                            int cmp = NvidiaVersionHelper.Compare(effectiveVersion, item.LatestVersion);
                            item.Status = cmp < 0 ? GpuDriverStatus.UpdateAvailable : GpuDriverStatus.UpToDate;
                        }
                    }
                    else
                    {
                        // Undocumented NVIDIA lookup returned nothing usable -
                        // never present a stale cache as current fact.
                        item.ErrorMessage = "Unable to check - NVIDIA lookup unavailable. Use the vendor page.";
                        item.Status = GpuDriverStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    item.ErrorMessage = $"Unable to check - NVIDIA lookup unavailable: {ex.Message}";
                    item.Status = GpuDriverStatus.Failed;
                }
                return;
            }

            if (item.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
            {
                item.Status = GpuDriverStatus.Downloading;
                try
                {
                    var amdApi = new AmdDriverApiService();
                    var variant = NotebookGpu ? AmdDriverApiService.AmdPackageVariant.Notebook : AmdDriverApiService.AmdPackageVariant.Desktop;
                    var driverInfo = await amdApi.GetLatestDriverAsync(variant, _cts?.Token ?? CancellationToken.None);
                    if (driverInfo != null)
                    {
                        item.LatestVersion = driverInfo.Version;
                        item.DownloadUrl = driverInfo.DownloadUrl;
                        item.Status = string.IsNullOrEmpty(item.InstalledVersion) ? GpuDriverStatus.NotInstalled : GpuDriverStatus.UpdateAvailable;
                        item.ErrorMessage = string.Empty;
                    }
                    else
                    {
                        item.ErrorMessage = "AMD API Lookup failed.";
                        item.Status = GpuDriverStatus.Failed;
                    }
                }
                catch (Exception ex)
                {
                    item.ErrorMessage = $"AMD Lookup failed: {ex.Message}";
                    item.Status = GpuDriverStatus.Failed;
                }
                return;
            }
        }

        /// <summary>
        /// Unique temp path per download (GUID suffix) - a fixed name like
        /// "nvidia_driver.exe" gets locked by a crashed prior run, antivirus
        /// scanning, or a still-running installer, and FileStream(Create) then
        /// throws "file being used by another process".
        /// </summary>
        private static string TempDownloadPath(string baseName)
        {
            string stem = Path.GetFileNameWithoutExtension(baseName);
            string ext = Path.GetExtension(baseName);
            if (string.IsNullOrEmpty(stem)) stem = "driver_download";
            return Path.Combine(Path.GetTempPath(), $"{stem}_{Guid.NewGuid():N}{ext}");
        }

        [RelayCommand]
        private async Task InstallDriverAsync(GpuDriverItem? item)
        {
            if (item is null) return;
            SelectedDriver = item;

            if (item.IsPageOnly)
            {
                GpuDriverService.OpenUrl(item.VendorPageUrl);
                return;
            }

            // Real UAC-sensitive operations follow - confirm first with a
            // summary of what is about to happen.
            bool confirmed = await ConfirmInstallAsync(item);
            if (!confirmed) return;

            _cts = new CancellationTokenSource();
            IsInstalling = true;
            InstallStatusText = "Initializing...";

            IProgress<GpuDriverStatus> progress = new Progress<GpuDriverStatus>(status =>
            {
                item.Status = status;
                InstallStatusText = item.StatusText;
                if (status is GpuDriverStatus.Installed or GpuDriverStatus.Failed or GpuDriverStatus.ManualActionRequired)
                    IsInstalling = false;
            });
            IProgress<double> downloadProgress = new Progress<double>(val =>
            {
                item.DownloadProgress = val;
                InstallStatusText = $"Downloading... ({val:F1}%)";
            });
            IProgress<string> errorProgress = new Progress<string>(error => item.ErrorMessage = error);
            
            void logProgress(string msg)
            {
                System.Diagnostics.Debug.WriteLine(msg);
                InstallStatusText = msg;
            }

            try
            {                if (item.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    await InstallNvidiaModernAsync(item, progress, downloadProgress, logProgress, _cts.Token);
                    IsInstalling = false;
                    _cts.Dispose();
                    _cts = null;
                    return;

                    // Legacy inline pipeline removed: NVIDIA installs now go
                    // through InstallNvidiaModernAsync (verify → extract →
                    // component picker → elevated setup.exe). See Part 2-6.
                }
                else if (item.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.DownloadUrl))
                    {
                        item.ErrorMessage = "No URL identified for AMD.";
                        item.Status = GpuDriverStatus.Failed;
                        IsInstalling = false;
                        _cts.Dispose();
                        _cts = null;
                        return;
                    }

                    progress.Report(GpuDriverStatus.Downloading);
                    string tempPath = TempDownloadPath(item.InstallerFileName);
                    
                    using var response = await _http.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    response.EnsureSuccessStatusCode();
                    
                    using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, true))
                        await contentStream.CopyToAsync(fileStream, _cts.Token);
                    
                    string extractDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AMD_Extract");
                    System.IO.Directory.CreateDirectory(extractDir);
                    
                    progress.Report(GpuDriverStatus.Installing);
                    
                    bool extracted = await _amdService.ExtractInstallerAsync(tempPath, extractDir, logProgress, _cts.Token);
                    if (!extracted) { item.ErrorMessage = "AMD Extraction failed."; progress.Report(GpuDriverStatus.Failed); return; }
                    
                    progress.Report(GpuDriverStatus.Installing);
                    
                    string logPath = Path.Combine(Path.GetTempPath(), "AMDInstall.log");
                    bool success = await _amdService.InstallCustomizedAsync(extractDir, logPath, logProgress, _cts.Token);
                    
                    if (success) progress.Report(GpuDriverStatus.Installed);
                    else { item.ErrorMessage = "Install failed or threw errors."; progress.Report(GpuDriverStatus.Failed); }
                }
            }
            catch (OperationCanceledException)
            {
                item.ErrorMessage = "Cancelled";
                item.Status = GpuDriverStatus.Failed;
            }

            IsInstalling = false;
            _cts?.Dispose();
            _cts = null;
        }

        [RelayCommand]
        private void OpenVendorPage(GpuDriverItem? item)
        {
            if (item is null) return;
            GpuDriverService.OpenUrl(item.VendorPageUrl);
        }

        [RelayCommand]
        private void CancelInstall()
        {
            _cts?.Cancel();
        }

        /// <summary>
        /// Full NVIDIA pipeline: download (nvidia.com-enforced) → WinVerifyTrust
        /// signature gate → 7z extraction → component picker dialog (Part 4) →
        /// setup.cfg rewrite → elevated silent setup.exe. All failures surface
        /// in the dialog; nothing executes without passing verification.
        /// </summary>
        private async Task InstallNvidiaModernAsync(
            GpuDriverItem item, IProgress<GpuDriverStatus> progress,
            IProgress<double> downloadProgress, Action<string> log, CancellationToken ct)
        {
            // Resolve URL if the check hasn't run yet. Prefer the PCI device
            // ID (works even for a driverless card Windows can't name); fall
            // back to the product-name lookup.
            if (string.IsNullOrEmpty(item.DownloadUrl))
            {
                var gpu = DetectedGpus.FirstOrDefault(g => g.Vendor.Equals("NVIDIA"));
                if (gpu is not null)
                {
                    var best = await _nvidiaService.GetDriversByDeviceIdAsync(gpu.PnpDeviceId ?? "", 1, StudioChannel, ct);
                    if (best.Count == 0)
                        best = await _nvidiaService.GetDriversAsync(gpu.Name, 1, StudioChannel, NotebookGpu, ct);
                    if (best.Count > 0) item.DownloadUrl = best[0].DownloadUrl;
                }
            }
            if (string.IsNullOrEmpty(item.DownloadUrl))
            {
                item.ErrorMessage = "Unable to resolve a download URL. Run Check for updates first.";
                item.Status = GpuDriverStatus.Failed;
                return;
            }
            if (!NvidiaPackageService.IsAllowedUrl(item.DownloadUrl))
            {
                item.ErrorMessage = "Refusing download: URL is not an https://*.nvidia.com location.";
                item.Status = GpuDriverStatus.Failed;
                return;
            }

            var xamlRoot = App.MainWindow?.Content?.XamlRoot;
            if (xamlRoot is null)
            {
                item.ErrorMessage = "Window not available.";
                item.Status = GpuDriverStatus.Failed;
                return;
            }

            // The dialog opens immediately and tracks the whole pipeline:
            // download → verify → extract → component list → install.
            var dialog = new NvidiaComponentPickerDialog(xamlRoot);
            var installTcs = new TaskCompletionSource();
            var dialogDispatch = dialog.DispatcherQueue;

            void DialogStatus(string m) => dialogDispatch.TryEnqueue(() => dialog.ReportStatus(m));
            void DialogProgress(double p) => dialogDispatch.TryEnqueue(() => dialog.ReportDownloadProgress(p));
            void DialogFail(string m)
            {
                dialogDispatch.TryEnqueue(() => dialog.FailEarly(m));
                item.ErrorMessage = m;
                item.Status = GpuDriverStatus.Failed;
            }

            _ = dialog.ShowAsync();

            item.Status = GpuDriverStatus.Downloading;
            DialogStatus($"Downloading the official package ({item.LatestVersion}) from nvidia.com…");
            string packagePath;
            try
            {
                packagePath = await _packageService.DownloadAsync(item.DownloadUrl, new Progress<double>(p =>
                {
                    downloadProgress.Report(p);
                    DialogProgress(p);
                }), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DialogFail($"Download failed: {ex.Message}");
                return;
            }

            // Part 2 gate: signature verification BEFORE anything touches the file.
            DialogStatus("Verifying NVIDIA digital signature…");
            InstallProgressIndeterminate(dialog);
            var (info, verifyError) = await _packageService.VerifyAsync(packagePath, ct);
            if (info is null || verifyError is not null)
            {
                DialogFail($"Package rejected: {verifyError}. The package was not extracted or executed.");
                TryDelete(packagePath);
                return;
            }
            dialogDispatch.TryEnqueue(() => dialog.SetVerification(info));
            DialogStatus($"Signature valid: {info.SignatureSubject}");

            // Part 3: extraction (7z SFX).
            DialogStatus("Extracting package contents…");
            InstallProgressIndeterminate(dialog);
            string extractDir;
            try
            {
                extractDir = await _packageService.ExtractAsync(packagePath, log, ct);
            }
            catch (Exception ex)
            {
                DialogFail($"Extraction failed: {ex.Message}");
                return;
            }

            // Part 3/4: parse setup.cfg into the component list.
            DialogStatus("Reading component list from setup.cfg…");
            List<NvidiaComponent> components;
            try
            {
                components = await _packageService.ParseComponentsAsync(extractDir, log);
            }
            catch (Exception ex)
            {
                DialogFail($"Component parsing failed: {ex.Message}");
                return;
            }
            if (components.Count == 0)
            {
                DialogFail("No installable components found in the package.");
                return;
            }

            // Hand the dialog over to the user for component selection.
            dialogDispatch.TryEnqueue(() => dialog.SetComponents(components));
            item.Status = GpuDriverStatus.UpdateAvailable; // back to actionable; dialog owns the rest

            dialog.InstallRequested += async (_, request) =>
            {
                try
                {
                    // Apply the selection to setup.cfg.
                    await _packageService.ApplySelectionAsync(extractDir, dialog.Result!.Value.Components,
                        DialogStatus);

                    // Part 5: elevated silent install of the kept components.
                    var (exit, reboot, _) = await _packageService.InstallAsync(
                        extractDir, request.CleanInstall, DialogStatus, ct);

                    bool success = exit == 0 || reboot;
                    dialog.CompleteInstall(success, reboot,
                        success
                            ? (reboot ? "Install complete - a restart is required." : "Install complete.")
                            : $"Installer failed with exit code {exit}. The extracted package was kept at {NvidiaPackageService.TempRoot} for inspection.");

                    if (success)
                    {
                        item.ErrorMessage = string.Empty;
                        await _gpuService.RefreshInstalledVersionAsync(item, "NVIDIA", ct);
                        progress.Report(reboot ? GpuDriverStatus.Installed : GpuDriverStatus.Installed);
                        NvidiaPackageService.CleanupTemp(keepForDebug: false);
                    }
                    else
                    {
                        item.ErrorMessage = $"Install failed (exit {exit}).";
                        item.Status = GpuDriverStatus.Failed;
                        NvidiaPackageService.CleanupTemp(keepForDebug: true);
                    }
                }
                catch (OperationCanceledException) { /* cancelled */ }
                catch (Exception ex)
                {
                    dialog.CompleteInstall(false, false, $"Install error: {ex.Message}");
                    item.ErrorMessage = $"Install error: {ex.Message}";
                    item.Status = GpuDriverStatus.Failed;
                }
                finally
                {
                    installTcs.TrySetResult();
                }
            };

            await installTcs.Task; // keep IsInstalling until the dialog finishes
        }

        private static void InstallProgressIndeterminate(NvidiaComponentPickerDialog dialog)
            => dialog.DispatcherQueue.TryEnqueue(() =>
            {
                dialog.SetIndeterminate();
            });

        /// <summary>
        /// Pre-install confirmation dialog: version transition summary plus a
        /// screen-flicker warning. Also the gate that keeps the install from
        /// ever starting without explicit user consent.
        /// </summary>
        private static async Task<bool> ConfirmInstallAsync(GpuDriverItem item)
        {
            var window = App.MainWindow;
            var xamlRoot = window?.Content?.XamlRoot;
            if (xamlRoot is null) return false;

            string transition = string.IsNullOrEmpty(item.InstalledVersion)
                ? $"install driver {item.LatestVersion}"
                : $"update from {item.InstalledVersion} to {item.LatestVersion}";

            var dialog = new ContentDialog
            {
                Title = "Install display driver",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Ready to {transition} ({item.Vendor}).",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = "The screen may flicker or go black briefly during installation. " +
                                   "Close games and save your work before continuing.",
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources
                                ["TextFillColorSecondaryBrush"],
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
                PrimaryButtonText = "Install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        /// <summary>Best-effort delete: AV scanners often hold a fresh file briefly.</summary>
        private static void TryDelete(string path)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(250 * (attempt + 1));
                }
                catch { return; }
            }
        }
    }
}
