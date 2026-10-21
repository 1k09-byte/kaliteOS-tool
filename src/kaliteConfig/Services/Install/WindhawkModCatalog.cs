using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace kaliteConfig.Services;

public static class WindhawkModCatalog
{
    public static IReadOnlyList<string> AllowedModIds
    {
        get
        {
            var path = BundledBackupPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Array.Empty<string>();

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var mods = doc.RootElement.GetProperty("mods");
                var ids = new List<string>(mods.GetArrayLength());
                foreach (var mod in mods.EnumerateArray())
                    if (mod.TryGetProperty("modId", out var id) &&
                        id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
                return ids;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public static string? BundledBackupPath
    {
        get
        {
            var assemblyDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(assemblyDir)) return null;

            foreach (var candidate in new[]
            {
                Path.Combine(assemblyDir, "Assets", "Windhawk", "KaliteOS.json"),
                Path.Combine(assemblyDir, "Assets", "Windhawk", "mods-bundled.json"),
                Path.Combine(assemblyDir, "Assets", "KaliteOS.json"),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    public static string EnsureBundledBackupOnDisk()
    {
        var source = BundledBackupPath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
            throw new FileNotFoundException(
                "The bundled Windhawk mod list (Assets/Windhawk/mods-bundled.json) is missing from the installation.");

        var assemblyDir = AppContext.BaseDirectory;

        var targetDir = Path.Combine(assemblyDir, "Assets", "Windhawk");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "mods-bundled.json");

        if (!File.Exists(target) ||
            File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
        {
            File.Copy(source, target, overwrite: true);
        }

        return target;
    }
}
