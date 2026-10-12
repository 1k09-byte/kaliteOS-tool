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
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.Services;

public sealed class InstallMonitorSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public HashSet<string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RegistryKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstallMonitorDiff
{
    public List<string> AddedFiles { get; set; } = new();
    public List<string> AddedRegistryKeys { get; set; } = new();
}

public sealed class InstallMonitorService
{
    private static readonly string[] MonitorDirectories = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    };

    private static readonly string[] MonitorRegistryHives = new[]
    {
        @"SOFTWARE",
        @"SOFTWARE\WOW6432Node"
    };

    public Task<InstallMonitorSnapshot> CreateSnapshotAsync(IProgress<int> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var snapshot = new InstallMonitorSnapshot();
            
            // 1. Files
            int dirCount = MonitorDirectories.Length;
            for (int i = 0; i < dirCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dir = MonitorDirectories[i];
                if (Directory.Exists(dir))
                {
                    try
                    {
                        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                        foreach (var f in files) snapshot.Files.Add(f);
                    } catch { } // Ignore locked folders
                }
                progress.Report((i + 1) * 50 / dirCount); // 50% for files
            }

            // 2. Registry
            int hiveCount = MonitorRegistryHives.Length * 2; // HKLM + HKCU
            int currentHive = 0;
            
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var hive in MonitorRegistryHives)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = baseKey.OpenSubKey(hive);
                        if (key != null)
                        {
                            ScanRegistryTree(baseKey.Name, hive, key, snapshot.RegistryKeys);
                        }
                    } catch { }
                    currentHive++;
                    progress.Report(50 + (currentHive * 50 / hiveCount));
                }
            }

            progress.Report(100);
            return snapshot;
        }, ct);
    }

    private void ScanRegistryTree(string basePath, string currentPath, RegistryKey key, HashSet<string> keys)
    {
        string fullPath = $@"{basePath}\{currentPath}";
        keys.Add(fullPath);

        try
        {
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey != null)
                    {
                        ScanRegistryTree(basePath, $@"{currentPath}\{subKeyName}", subKey, keys);
                    }
                }
                catch { } // Ignore protected keys
            }
        }
        catch { }
    }

    public Task<InstallMonitorDiff> CompareSnapshotsAsync(InstallMonitorSnapshot before, InstallMonitorSnapshot after, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var diff = new InstallMonitorDiff();
            
            // Calculate added files
            foreach (var file in after.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!before.Files.Contains(file))
                {
                    diff.AddedFiles.Add(file);
                }
            }

            // Calculate added registry keys
            foreach (var key in after.RegistryKeys)
            {
                ct.ThrowIfCancellationRequested();
                if (!before.RegistryKeys.Contains(key))
                {
                    diff.AddedRegistryKeys.Add(key);
                }
            }

            return diff;
        }, ct);
    }
}
