using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>
    /// JSON-backed CRUD for named profiles in %LOCALAPPDATA%\kaliteConfig.
    /// Versioned schema (SchemaVersion) so future fields don't break old saves:
    /// unknown properties are ignored on load, missing ones keep defaults.
    /// </summary>
    public sealed class ProfileStorageService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _dir;
        private readonly object _gate = new();

        public ProfileStorageService(string? directory = null)
        {
            _dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kaliteConfig", "gpu-overclock");
            Directory.CreateDirectory(_dir);
        }

        private string PathFor(OverclockProfile p) => Path.Combine(_dir, $"{p.Id:N}.json");

        public IReadOnlyList<OverclockProfile> LoadAll()
        {
            var list = new List<OverclockProfile>();
            lock (_gate)
            {
                foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<OverclockProfile>(File.ReadAllText(file), JsonOpts);
                        if (p != null)
                        {
                            if (p.SchemaVersion > OverclockProfile.CurrentSchemaVersion)
                            {
                                // Written by a newer app version — load but don't
                                // re-save, so we never destroy newer data.
                                p.Name = $"[newer version] {p.Name}";
                            }
                            list.Add(p);
                        }
                    }
                    catch
                    {
                        // A corrupt profile file must not break the module.
                        try { File.Move(file, file + ".corrupt", overwrite: true); } catch { }
                    }
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public void Save(OverclockProfile profile)
        {
            profile.SchemaVersion = OverclockProfile.CurrentSchemaVersion;
            lock (_gate)
            {
                File.WriteAllText(PathFor(profile), JsonSerializer.Serialize(profile, JsonOpts));
            }
        }

        public void Delete(OverclockProfile profile)
        {
            lock (_gate)
            {
                var path = PathFor(profile);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
