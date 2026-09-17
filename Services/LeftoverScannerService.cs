using Microsoft.Win32;
using kaliteConfig.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class LeftoverScannerService
{
    private static readonly string[] SearchDirectories = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) // ProgramData
    };

    private static readonly string[] SearchRegistryHives = new[]
    {
        @"SOFTWARE",
        @"SOFTWARE\WOW6432Node"
    };

    public Task<List<string>> ScanLeftoversAsync(UninstallerItem item)
    {
        return Task.Run(() =>
        {
            var leftovers = new List<string>();
            var targets = new List<string> { item.Name };
            if (!string.IsNullOrWhiteSpace(item.Publisher) && !IsGenericPublisher(item.Publisher))
            {
                targets.Add(item.Publisher);
            }

            // 1. Filesystem scan
            foreach (var rootDir in SearchDirectories)
            {
                if (!Directory.Exists(rootDir)) continue;
                
                try
                {
                    var dirs = Directory.GetDirectories(rootDir);
                    foreach (var dir in dirs)
                    {
                        string dirName = Path.GetFileName(dir);
                        foreach (var target in targets)
                        {
                            if (dirName.Contains(target, StringComparison.OrdinalIgnoreCase))
                            {
                                leftovers.Add(dir);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Registry scan (HKCU and HKLM)
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var hive in SearchRegistryHives)
                {
                    try
                    {
                        using var key = baseKey.OpenSubKey(hive);
                        if (key == null) continue;

                        var subkeys = key.GetSubKeyNames();
                        foreach (var subKeyName in subkeys)
                        {
                            foreach (var target in targets)
                            {
                                if (subKeyName.Contains(target, StringComparison.OrdinalIgnoreCase))
                                {
                                    leftovers.Add($@"{baseKey.Name}\{hive}\{subKeyName}");
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 3. Return distinct explicitly identified paths
            return leftovers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    public Task CleanLeftoversAsync(List<string> paths)
    {
        return Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (path.StartsWith("HKEY_"))
                    {
                        var parts = path.Split('\\', 2);
                        RegistryKey baseKey = parts[0] == "HKEY_LOCAL_MACHINE" ? Registry.LocalMachine : Registry.CurrentUser;
                        using var bk = RegistryKey.OpenBaseKey(baseKey == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
                        bk.DeleteSubKeyTree(parts[1], false);
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch { } // Best effort deletion
            }
        });
    }

    private static bool IsGenericPublisher(string publisher)
    {
        var blacklist = new[] { "Microsoft", "Intel", "NVIDIA", "Advanced Micro Devices", "Adobe", "Apple" };
        return blacklist.Any(x => publisher.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
}
