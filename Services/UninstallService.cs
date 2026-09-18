using Microsoft.Win32;
using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class UninstallService
{
    private readonly string[] _registryHives =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public Task<List<UninstallerItem>> GetAllAppsAsync(CancellationToken ct, IProgress<string>? progress = null)
    {
        return Task.Run(() =>
        {
            var apps = new List<UninstallerItem>();
            progress?.Report("Reading installed programs from the registry…");
            apps.AddRange(GetRegistryApps());
            if (ct.IsCancellationRequested) return new List<UninstallerItem>();
            progress?.Report("Reading Microsoft Store packages…");
            apps.AddRange(GetAppXPackages());
            
            // Note: MSIs are usually listed under the registry keys as well natively.
            // Therefore, GetRegistryApps often encompasses MSI installations if we 
            // identify the UninstallString correctly (msiexec /x {GUID}).

            // Optional: Deduplicate by Name
            var deduped = new Dictionary<string, UninstallerItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
            {
                if (!deduped.ContainsKey(app.Name))
                {
                    deduped[app.Name] = app;
                }
            }

            return new List<UninstallerItem>(deduped.Values);
        }, ct);
    }

    private IEnumerable<UninstallerItem> GetRegistryApps()
    {
        var items = new List<UninstallerItem>();

        foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var hive in _registryHives)
            {
                using var key = baseKey.OpenSubKey(hive);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var sysComponent = subKey.GetValue("SystemComponent") as int?;
                    bool isSystem = sysComponent == 1;

                    var parentName = subKey.GetValue("ParentKeyName") as string;
                    if (!string.IsNullOrEmpty(parentName)) continue;

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    var uninstallString = subKey.GetValue("UninstallString") as string;
                    var quietUninstallString = subKey.GetValue("QuietUninstallString") as string;
                    // Keep entries even without an uninstaller so Force Remove can clean them.
                    long estBytes = 0;
                    var estRaw = subKey.GetValue("EstimatedSize");
                    if (estRaw is int ei) estBytes = (long)ei * 1024L;

                    var isMsi = uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true || 
                                quietUninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true;

                    items.Add(new UninstallerItem
                    {
                        Id = subKeyName,
                        Name = displayName,
                        Publisher = subKey.GetValue("Publisher") as string ?? string.Empty,
                        Version = subKey.GetValue("DisplayVersion") as string ?? string.Empty,
                        InstallDate = subKey.GetValue("InstallDate") as string ?? string.Empty,
                        InstallLocation = subKey.GetValue("InstallLocation") as string ?? string.Empty,
                        UninstallString = uninstallString ?? string.Empty,
                        QuietUninstallString = quietUninstallString ?? string.Empty,
                        InstallType = isMsi ? InstallType.Msi : InstallType.Registry,
                        EstimatedSize = estBytes,
                        IsSystemComponent = isSystem,
                        DisplayIcon = subKey.GetValue("DisplayIcon") as string ?? string.Empty,
                    });
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Package.IsStub needs Windows 10 2004+ (10.0.19041); the app supports
    /// back to 17763, so guard the call — older systems treat every package
    /// as non-stub.
    /// </summary>
    private static bool IsStubPackage(Windows.ApplicationModel.Package package)
    {
        if (!System.OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        return package.IsStub;
    }

    private IEnumerable<UninstallerItem> GetAppXPackages()
    {
        var items = new List<UninstallerItem>();
        
        try
        {
            // Windows RT dependency: using Windows.Management.Deployment namespace
            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty);
            
            foreach (var package in packages)
            {
                if (package.IsFramework || package.IsResourcePackage || IsStubPackage(package)) continue;
                
                try
                {
                    var id = package.Id;
                    var name = id.Name;
                    var publisher = id.Publisher;
                    var version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}";
                    string installPath = string.Empty;
                    try { installPath = package.InstalledLocation.Path; } catch { }
                    // Resolve the Store logo to a real file so the icon loader shows it.
                    string logoFile = string.Empty;
                    try
                    {
                        var logoUri = package.Logo; // e.g. ms-appx:///Assets/Logo.png
                        var rel = logoUri.ToString()
                            .Replace("ms-appx:///", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace("ms-appx://", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .TrimStart('/')
                            .Replace('/', Path.DirectorySeparatorChar);
                        var candidate = Path.Combine(installPath, rel);
                        if (File.Exists(candidate)) logoFile = candidate;
                    }
                    catch { }

                    items.Add(new UninstallerItem
                    {
                        Id = id.FamilyName,
                        Name = name,
                        Publisher = publisher,
                        Version = version,
                        InstallLocation = installPath,
                        DisplayIcon = logoFile,
                        UninstallString = $"Remove-AppxPackage -Package \"{id.FullName}\"", 
                        InstallType = InstallType.AppX,
                    });
                }
                catch { }
            }
        }
        catch { } // Can fail if not packaged or SDK missing references, fallback gracefully

        return items;
    }

    public Task<Dictionary<string, long>> ComputeRealSizesAsync(IEnumerable<UninstallerItem> items, CancellationToken ct)
    {
        // NOTE: returns id->size and never touches bound objects: assigning
        // ComputedSize off the UI thread would raise PropertyChanged on the
        // wrong thread (RPC_E_WRONG_THREAD crash). Caller applies on UI thread.
        return Task.Run(() =>
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (item.ComputedSize > 0) continue;
                var dir = item.InstallLocation?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    { try { total += new FileInfo(f).Length; } catch { } }
                    // one level deep to stay fast; full deep scan is the LeftoverScanner's job
                    if (total > 0) result[item.Id] = total;
                }
                catch { }
            }
            return result;
        }, ct);
    }

    /// <summary>Runs the registered uninstaller. Returns null on success,
    /// or a human-readable error (also when there is nothing to run).</summary>
    public string? Uninstall(UninstallerItem item)
    {
        try
        {
            if (item.InstallType == InstallType.AppX)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{item.UninstallString}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p == null ? "Could not start the Store removal." : null;
            }

            string cmd = !string.IsNullOrEmpty(item.QuietUninstallString) ? item.QuietUninstallString : item.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd))
                return "No registered uninstaller — use Force Remove.";

            if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                // /I (advertise/repair) combined with /passive is rejected by
                // msiexec — silently. Real removal needs /X.
                cmd = System.Text.RegularExpressions.Regex.Replace(
                    cmd, @"/I\s*", "/X ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // Ensure passive mode for MSI to behave pseudo-headless if not deeply quiet
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cmd} /passive /norestart",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p == null ? "Could not start msiexec." : null;
            }

            if (!SplitCommand(cmd, out string exePath, out string args))
                return $"Uninstaller file not found: {exePath}";

            var exePsi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            };
            using var proc = Process.Start(exePsi);
            proc?.WaitForExit();
            return proc == null ? $"Could not start: {exePath}" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Deletes the app's registry uninstall entries (HKLM, HKLM\WOW6432Node,
    /// HKCU) so it disappears from "Installed apps" lists. Returns how many
    /// keys were removed.</summary>
    public int RemoveRegistryEntry(UninstallerItem item)
    {
        int removed = 0;
        foreach (var (root, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            foreach (var hive in _registryHives)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(root, view);
                    using var key = baseKey.OpenSubKey(hive, writable: true);
                    if (key == null) continue;
                    // Id is the subkey name as enumerated; also sweep same-named
                    // keys in case the entry lives in the other bitness hive.
                    if (key.GetSubKeyNames().Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        try { key.DeleteSubKeyTree(item.Id); removed++; } catch { }
                    }
                }
                catch { }
            }
        }
        return removed;
    }

    /// <summary>Deletes the app's install directory if it still exists.
    /// Returns true when a directory was removed.</summary>
    public bool RemoveInstallDirectory(UninstallerItem item)
    {
        try
        {
            var dir = item.InstallLocation?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Splits an uninstall command into exe + arguments.
    /// Returns false when the target file does not exist.</summary>
    internal static bool SplitCommand(string cmd, out string exe, out string args)
    {
        exe = string.Empty;
        args = string.Empty;
        cmd = (cmd ?? string.Empty).Trim();
        if (cmd.Length == 0) return false;
        if (File.Exists(cmd)) { exe = cmd; return true; }
        if (cmd.StartsWith("\""))
        {
            int q = cmd.IndexOf('"', 1);
            if (q <= 0) return false;
            exe = cmd.Substring(1, q - 1);
            args = cmd.Substring(q + 1).Trim();
            return File.Exists(exe);
        }
        int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            exe = cmd.Substring(0, exeIdx + 4).Trim().Trim('"');
            args = cmd.Substring(exeIdx + 4).Trim();
            return File.Exists(exe);
        }
        int sp = cmd.IndexOf(' ');
        if (sp > 0)
        {
            exe = cmd.Substring(0, sp).Trim('"');
            args = cmd.Substring(sp + 1).Trim();
        }
        else exe = cmd;
        return File.Exists(exe);
    }
}
