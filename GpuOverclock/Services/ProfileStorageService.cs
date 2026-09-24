using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                                // Written by a newer app version - load but don't
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

            // Stamp the startup-default designation onto the UI projections.
            var defaultName = GetDefaultProfileName();
            if (defaultName is not null)
            {
                foreach (var p in list.Where(p => p.Name == defaultName))
                    p.IsStartupDefault = true;
            }
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
                // A deleted profile can no longer be the startup default.
                if (GetDefaultProfileName() == profile.Name) ClearDefaultProfile();
            }
        }

        // -------- startup default designation (phase 5) --------

        // .marker (not .json): LoadAll enumerates *.json and must never see
        // the designation file as a phantom profile.
        private string DefaultProfilePath => Path.Combine(_dir, "startup-default.marker");

        private sealed record DefaultProfileFile(string Name);

        /// <summary>Name of the profile designated for startup reapply, or null.</summary>
        public string? GetDefaultProfileName()
        {
            try
            {
                if (!File.Exists(DefaultProfilePath)) return null;
                var file = JsonSerializer.Deserialize<DefaultProfileFile>(File.ReadAllText(DefaultProfilePath), JsonOpts);
                return string.IsNullOrWhiteSpace(file?.Name) ? null : file.Name;
            }
            catch { return null; }
        }

        public void SetDefaultProfile(string name)
        {
            lock (_gate)
            {
                File.WriteAllText(DefaultProfilePath,
                    JsonSerializer.Serialize(new DefaultProfileFile(name), JsonOpts));
            }
        }

        public void ClearDefaultProfile()
        {
            try { if (File.Exists(DefaultProfilePath)) File.Delete(DefaultProfilePath); }
            catch { }
        }

        // -------- per-game bindings (v2 Part B) --------

        // .bindings (NOT .json): LoadAll enumerates *.json and would try to
        // parse a bindings document as a profile - then quarantine it as
        // .corrupt. Same reason the default marker is .marker, not .json.
        private string BindingsPath => Path.Combine(_dir, "game-bindings.bindings");

        /// <summary>All saved game/profile bindings, oldest first.</summary>
        public IReadOnlyList<GameProfileBinding> LoadBindings()
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(BindingsPath)) return Array.Empty<GameProfileBinding>();
                    var list = JsonSerializer.Deserialize<List<GameProfileBinding>>(
                        File.ReadAllText(BindingsPath), JsonOpts);
                    if (list is null) return Array.Empty<GameProfileBinding>();
                    list.Sort((a, b) => DateTime.Compare(a.CreatedAt, b.CreatedAt));
                    return list;
                }
                catch
                {
                    try { File.Move(BindingsPath, BindingsPath + ".corrupt", overwrite: true); } catch { }
                    return Array.Empty<GameProfileBinding>();
                }
            }
        }

        /// <summary>Insert or replace by Id.</summary>
        public void SaveBinding(GameProfileBinding binding)
        {
            lock (_gate)
            {
                var list = LoadBindings().ToList();
                var idx = list.FindIndex(b => b.Id == binding.Id);
                if (idx >= 0) list[idx] = binding;
                else list.Add(binding);
                File.WriteAllText(BindingsPath, JsonSerializer.Serialize(list, JsonOpts));
            }
        }

        public void DeleteBinding(Guid id)
        {
            lock (_gate)
            {
                var list = LoadBindings().ToList();
                if (list.RemoveAll(b => b.Id == id) > 0)
                    File.WriteAllText(BindingsPath, JsonSerializer.Serialize(list, JsonOpts));
            }
        }

        /// <summary>Resolves a binding's profile by Id (null when deleted since binding).</summary>
        public OverclockProfile? FindProfile(Guid id)
        {
            lock (_gate)
            {
                foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<OverclockProfile>(File.ReadAllText(file), JsonOpts);
                        if (p is not null && p.Id == id) return p;
                    }
                    catch { }
                }
                return null;
            }
        }
    }
}
