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
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using kaliteConfig.Models;

namespace kaliteConfig.Services
{
    public class FirefoxExtensionInstaller
    {
        /// <summary>
        /// Adds AMO install URLs to the browser's distribution/policies.json.
        /// Returns false when the policy file could not be written, so callers can
        /// tell the user that extensions were not applied.
        /// </summary>
        public bool AddInstallUrls(string distributionPath, IEnumerable<string> urls)
            => WriteInstallUrls(distributionPath, urls, managedUrls: null);

        /// <summary>
        /// Makes the policy file match the current selection: managed URLs that are
        /// no longer selected are removed, new ones are added. URLs that this app
        /// does not manage are left untouched.
        /// </summary>
        public bool SetInstallUrls(string distributionPath, IEnumerable<string> managedUrls, IEnumerable<string> selectedUrls)
            => WriteInstallUrls(distributionPath, selectedUrls, managedUrls);

        private bool WriteInstallUrls(string distributionPath, IEnumerable<string> urls, IEnumerable<string>? managedUrls)
        {
            try
            {
                Directory.CreateDirectory(distributionPath);
                string policyPath = Path.Combine(distributionPath, "policies.json");

                JsonObject root;
                if (File.Exists(policyPath))
                {
                    try
                    {
                        root = JsonNode.Parse(File.ReadAllText(policyPath))?.AsObject() ?? new JsonObject();
                    }
                    catch
                    {
                        root = new JsonObject();
                    }
                }
                else
                {
                    root = new JsonObject();
                }

                JsonObject? policies = root["policies"]?.AsObject();
                if (policies is null)
                {
                    policies = new JsonObject();
                    root["policies"] = policies;
                }

                JsonObject? extensions = policies["Extensions"]?.AsObject();
                if (extensions is null)
                {
                    extensions = new JsonObject();
                    policies["Extensions"] = extensions;
                }

                JsonArray? install = extensions["Install"]?.AsArray();
                if (install is null)
                {
                    install = new JsonArray();
                    extensions["Install"] = install;
                }

                var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var url in urls)
                {
                    if (!string.IsNullOrWhiteSpace(url)) desired.Add(url.Trim());
                }

                var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (managedUrls is not null)
                {
                    foreach (var url in managedUrls)
                    {
                        if (!string.IsNullOrWhiteSpace(url)) managed.Add(url.Trim());
                    }
                }

                bool changed = false;

                if (managed.Count > 0)
                {
                    for (int i = install.Count - 1; i >= 0; i--)
                    {
                        string? value = install[i] is JsonValue v ? v.GetValue<string>()?.Trim() : null;
                        if (value is null || !managed.Contains(value) || desired.Contains(value)) continue;
                        install.RemoveAt(i);
                        changed = true;
                    }
                }

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in install)
                {
                    string? s = node is JsonValue v ? v.GetValue<string>() : null;
                    if (!string.IsNullOrWhiteSpace(s))
                        existing.Add(s.Trim());
                }

                foreach (var url in desired)
                {
                    if (!existing.Add(url)) continue;
                    install.Add(url);
                    changed = true;
                }

                if (changed)
                    File.WriteAllText(policyPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void InstallExtensions(IEnumerable<ExtensionItem> extensions, string distributionPath)
        {
            try 
            {
                string installDir = Environment.ExpandEnvironmentVariables(distributionPath);
                if (!Directory.Exists(installDir))
                {
                    Directory.CreateDirectory(installDir);
                }

                string policyPath = Path.Combine(installDir, "policies.json");

                var extensionSettings = new Dictionary<string, object>();

                foreach (var ext in extensions)
                {
                    if (ext.IsSelected && ext.IsAvailable && !string.IsNullOrEmpty(ext.FirefoxAddonSlug))
                    {
                        string id = ext.FirefoxAddonSlug switch 
                        {
                            "ublock-origin" => "uBlock0@raymondhill.net",
                            "privacy-badger17" => "jid1-MnnxcxisBPnSXQ@jetpack",
                            "istilldontcareaboutcookies" => "idcac-pub@guus.ninja", 
                            "location-guard" => "jid1-HdwPLukcGQeOSh@jetpack",
                            "decentraleyes" => "jid1-BoFifL9Vbdl2zQ@jetpack",
                            "clearurls" => "{74145f27-f039-47ce-a470-a662b129930a}",
                            _ => $"{ext.FirefoxAddonSlug}@zen.extensions"
                        };

                        extensionSettings[id] = new 
                        {
                            installation_mode = "force_installed",
                            install_url = $"https://addons.mozilla.org/firefox/downloads/latest/{ext.FirefoxAddonSlug}/latest.xpi"
                        };
                    }
                }

                if (extensionSettings.Count > 0)
                {
                    var policyData = new { policies = new { ExtensionSettings = extensionSettings } };
                    string json = JsonSerializer.Serialize(policyData, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(policyPath, json);
                }
            } 
            catch (Exception)
            {
            }
        }
    }
}
