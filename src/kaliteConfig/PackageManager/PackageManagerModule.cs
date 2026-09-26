// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.Services;

namespace kaliteConfig.PackageManager
{
    /// <summary>
    /// Composition root for the package-manager module (mirrors the
    /// GpuOverclock module pattern): one instance per process, explicit
    /// services, no DI container. Observable so the nav update badge binds.
    /// </summary>
    public sealed partial class PackageManagerModule : ObservableObject
    {
        private static readonly Lazy<PackageManagerModule> _lazy = new(() => new PackageManagerModule());
        public static PackageManagerModule Instance => _lazy.Value;

        public PackageSourceRegistry Sources { get; }
        public PackageOperationQueue Queue { get; }
        public PackageBundleService Bundles { get; }

        [ObservableProperty] public partial int UpdateCount { get; set; }
        [ObservableProperty] public partial DateTime? UpdatesCheckedAt { get; set; }

        private PackageManagerModule()
        {
            Sources = new PackageSourceRegistry();
            Queue = new PackageOperationQueue();
            Bundles = new PackageBundleService();
        }

        /// <summary>
        /// Refreshes the nav badge count (best-effort; failures clear it).
        /// Called on page entry and after update runs.
        /// </summary>
        public async Task RefreshUpdateCountAsync()
        {
            try
            {
                var winget = Sources.Find(PackageSourceIds.WinGet);
                if (winget is null || !Sources.IsEnabled(winget) || !winget.CanListUpdates)
                {
                    UpdateCount = 0;
                    return;
                }
                var rows = await winget.GetAvailableUpdatesAsync();
                UpdateCount = rows.Count(r => r.HasUpdate);
                UpdatesCheckedAt = DateTime.Now;
            }
            catch
            {
                UpdateCount = 0;
            }
        }

        private Task<System.Collections.Generic.IReadOnlyList<PackageInfo>>? _installedCacheTask;

        /// <summary>
        /// Returns a cached list of installed packages, fast enough to use inside search algorithms.
        /// </summary>
        public Task<System.Collections.Generic.IReadOnlyList<PackageInfo>> GetInstalledPackagesAsync()
        {
            if (_installedCacheTask == null)
            {
                var winget = Sources.Find(PackageSourceIds.WinGet);
                if (winget != null && Sources.IsEnabled(winget) && winget.CanListInstalled)
                    _installedCacheTask = winget.GetInstalledAsync();
                else
                    _installedCacheTask = Task.FromResult((System.Collections.Generic.IReadOnlyList<PackageInfo>)System.Array.Empty<PackageInfo>());
            }
            return _installedCacheTask;
        }

        public void InvalidateInstalledCache()
        {
            _installedCacheTask = null;
        }
    }
}
