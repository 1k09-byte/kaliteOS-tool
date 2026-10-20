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
using System;
using System.Collections.Generic;
using kaliteConfig.Models;
using Microsoft.Win32;

namespace kaliteConfig.Services
{
    public class ChromiumExtensionInstaller
    {
        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, params string[] registryPaths)
            => InstallExtensions(extensions, DefaultUpdateUrl, registryPaths);

        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string updateUrl, params string[] registryPaths)
        {
            if (registryPaths == null || registryPaths.Length == 0) return;
            if (string.IsNullOrWhiteSpace(updateUrl)) updateUrl = DefaultUpdateUrl;

            var selected = new List<ExtensionItem>();
            foreach (var ext in extensions)
            {
                if (ext.IsSelected && ext.IsAvailable && !string.IsNullOrEmpty(ext.ChromiumExtensionId))
                    selected.Add(ext);
            }

            foreach (var registryPath in registryPaths)
            {
                TryWriteToHive(RegistryHive.LocalMachine, registryPath, selected, updateUrl);
                TryWriteToHive(RegistryHive.CurrentUser, registryPath, selected, updateUrl);
            }
        }

        public const string DefaultUpdateUrl = "https://clients2.google.com/service/update2/crx";

        public const string HeliumUpdateUrl = "https://clients2.9oo91e.qjz9zk/service/update2/crx";

        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string registryPath)
            => InstallExtensions(extensions, DefaultUpdateUrl, new[] { registryPath });

        private void TryWriteToHive(RegistryHive hive, string registryPath, List<ExtensionItem> selected, string updateUrl)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.CreateSubKey(registryPath, true);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    if (int.TryParse(valueName, out _))
                    {
                        try { key.DeleteValue(valueName, false); } catch { }
                    }
                }

                int index = 1;
                foreach (var ext in selected)
                {
                    key.SetValue(index.ToString(), $"{ext.ChromiumExtensionId};{updateUrl}", RegistryValueKind.String);
                    index++;
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
