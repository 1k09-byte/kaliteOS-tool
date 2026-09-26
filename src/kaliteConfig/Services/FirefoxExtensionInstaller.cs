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
        /// Reference-style (AutoOS) Firefox/Zen extension deploy: merges AMO install
        /// URLs into distribution\policies.json → policies.Extensions.Install[].
        /// Existing policies in the file are preserved; duplicate URLs are skipped.
        /// </summary>
        public void AddInstallUrls(string distributionPath, IEnumerable<string> urls)
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

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in install)
                {
                    // Non-string entries (left by other tools) must not abort the merge.
                    string? s = node is JsonValue v ? v.GetValue<string>() : null;
                    if (!string.IsNullOrWhiteSpace(s))
                        existing.Add(s.Trim());
                }

                bool changed = false;
                foreach (var url in urls)
                {
                    if (string.IsNullOrWhiteSpace(url) || !existing.Add(url.Trim())) continue;
                    install.Add(url.Trim());
                    changed = true;
                }

                if (changed)
                    File.WriteAllText(policyPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AddInstallUrls failed: {ex.Message}");
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
                // Silently swallow write failures to avoid breaking normal pipeline
            }
        }
    }
}
