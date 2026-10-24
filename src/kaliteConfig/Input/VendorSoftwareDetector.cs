using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace kaliteConfig.Input;

public interface IVendorSoftwareDetector
{
    (bool Present, string Name) Detect(MouseCatalog catalog);
}

public sealed class VendorSoftwareDetector : IVendorSoftwareDetector
{
    private static readonly string[] UninstallRoots = new[]
    {
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    };

    public (bool Present, string Name) Detect(MouseCatalog catalog)
    {
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try { processes.Add(process.ProcessName); }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
        }
        catch { }
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var service in ServiceController.GetServices())
            {
                try
                {
                    services.Add(service.ServiceName);
                    services.Add(service.DisplayName);
                }
                catch { }
                finally { try { service.Dispose(); } catch { } }
            }
        }
        catch { }
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in UninstallRoots)
        {
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = baseKey.OpenSubKey(root);
                    if (key is null) continue;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var entry = key.OpenSubKey(sub);
                            string? name = entry?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrWhiteSpace(name)) installed.Add(name);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        foreach (var program in catalog.VendorPrograms)
        {
            foreach (string hint in program.UninstallHints)
                foreach (string name in installed)
                    if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return (true, program.Name);
            foreach (string maker in program.MakerHints)
                foreach (string name in installed)
                    if (name.IndexOf(maker, StringComparison.OrdinalIgnoreCase) >= 0
                        && IsInputSoftware(name))
                        return (true, program.Name);
            foreach (string exe in program.Processes)
            {
                string stem = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? exe.Substring(0, exe.Length - 4) : exe;
                if (processes.Contains(stem)) return (true, program.Name);
            }
        }
        return (false, string.Empty);
    }

    private static bool IsInputSoftware(string displayName)
    {
        string text = displayName.ToLowerInvariant();
        return text.Contains("hub") || text.Contains("synapse") || text.Contains("engine")
            || text.Contains("icue") || text.Contains("swarm") || text.Contains("ngenuity")
            || text.Contains("core") || text.Contains("crate") || text.Contains("center")
            || text.Contains("gaming") || text.Contains("mouse");
    }
}
