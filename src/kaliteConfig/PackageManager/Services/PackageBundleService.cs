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
using System.Linq;
using System.Text;
using System.Text.Json;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>JSON bundle CRUD under %LOCALAPPDATA%\kaliteConfig\bundles.</summary>
public sealed class PackageBundleService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _dir;

    public PackageBundleService(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "bundles");
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<PackageBundle> LoadAll()
    {
        var list = new List<PackageBundle>();
        foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var bundle = JsonSerializer.Deserialize<PackageBundle>(File.ReadAllText(file), JsonOpts);
                if (bundle != null && !string.IsNullOrWhiteSpace(bundle.Name))
                    list.Add(bundle);
            }
            catch { /* a corrupt bundle file must not break the page */ }
        }
        return list.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(PackageBundle bundle)
    {
        bundle.Name = (bundle.Name ?? "").Trim();
        if (bundle.Name.Length == 0) throw new ArgumentException("Bundle needs a name.", nameof(bundle));
        foreach (char c in Path.GetInvalidFileNameChars())
            bundle.Name = bundle.Name.Replace(c, '_');
        File.WriteAllText(
            Path.Combine(_dir, bundle.Name + ".json"),
            JsonSerializer.Serialize(bundle, JsonOpts));
    }

    public void Delete(PackageBundle bundle)
    {
        try
        {
            string path = Path.Combine(_dir, bundle.Name + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>Writes the built-in Essentials bundle once. A marker file records
    /// that seeding happened, so deleting Essentials is permanent.</summary>
    public bool SeedEssentials()
    {
        try
        {
            string marker = Path.Combine(_dir, EssentialsBundle.MarkerFile);
            if (File.Exists(marker)) return false;
            if (LoadAll().Any(b => b.Name.Equals(EssentialsBundle.BundleName, StringComparison.OrdinalIgnoreCase)))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                return false;
            }
            Save(new PackageBundle { Name = EssentialsBundle.BundleName, Items = EssentialsBundle.Items });
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            return true;
        }
        catch { return false; }
    }

    public string ExportToJson(PackageBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOpts);

    public PackageBundle? ImportFromJson(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<PackageBundle>(json, JsonOpts);
            if (bundle == null || string.IsNullOrWhiteSpace(bundle.Name)) return null;
            bundle.Items ??= new List<BundleItem>();
            bundle.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.PackageId));
            return bundle;
        }
        catch { return null; }
    }

    /// <summary>
    /// Bundle items not currently installed (matched by id+source, case-insensitive).
    /// Pure logic - unit-tested.
    /// </summary>
    public static IReadOnlyList<BundleItem> DiffMissing(
        PackageBundle bundle, IEnumerable<PackageInfo> installed)
    {
        var have = new HashSet<string>(
            installed.Select(p => p.SourceId + "\u0001" + p.Id),
            StringComparer.OrdinalIgnoreCase);
        return bundle.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.PackageId)
                && !have.Contains(i.SourceId + "\u0001" + i.PackageId))
            .ToList();
    }
}

/// <summary>CSV export for package lists (updates view). Pure string building.</summary>
public static class PackageCsvExporter
{
    public static string Build(IEnumerable<PackageInfo> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Id,Installed,Available,Source,Publisher");
        foreach (var p in packages)
            sb.Append('"').Append(Cell(p.Name)).Append("\",\"")
              .Append(Cell(p.Id)).Append("\",\"")
              .Append(Cell(p.InstalledVersion)).Append("\",\"")
              .Append(Cell(p.AvailableVersion)).Append("\",\"")
              .Append(Cell(p.SourceLabel)).Append("\",\"")
              .Append(Cell(p.Publisher)).Append('"').AppendLine();
        return sb.ToString();

        static string Cell(string? s) => (s ?? "").Replace("\"", "\"\"");
    }
}
