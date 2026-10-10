// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kaliteConfig.Services;

/// <summary>
/// A user-chosen image painted behind the page content but above the animated
/// meteor field, with a controllable opacity and fit (Stretch).
///
/// Store format: JSON, one file per user, at
///   LocalApplicationData/kaliteConfig/background-image.json
///   { "Path": "C:\\Users\\…\\pic.png", "Opacity": 80, "Stretch": "Uniform" }
///
/// Everything is best-effort: a missing, malformed, or unreachable file falls
/// back to "no background image" silently. The image is decoded on the UI
/// thread (BitmapImage has UI-thread affinity) but the file I/O is async.
/// </summary>
internal static class BackgroundImageService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "background-image.json");

    public static readonly (string Path, byte Opacity, string Stretch) None =
        (string.Empty, 0, "Uniform");

    public enum StretchMode
    {
        None,
        Fill,
        Uniform,
        UniformToFill,
        Auto
    }

    /// <summary>
    /// The saved background-image preference, or <see cref="None"/> when there
    /// is none or it could not be read.
    /// </summary>
    public static (string Path, byte Opacity, string Stretch) Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return None;

            string text = File.ReadAllText(StorePath).Trim();
            if (text.Length == 0) return None;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? path = null;
            if (root.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                path = p.GetString();

            byte opacity = 100;
            if (root.TryGetProperty("Opacity", out var o) && o.ValueKind == JsonValueKind.Number)
                opacity = (byte)Math.Clamp(o.GetInt32(), 0, 100);

            string stretch = "Uniform";
            if (root.TryGetProperty("Stretch", out var s) && s.ValueKind == JsonValueKind.String)
                stretch = s.GetString() ?? stretch;

            if (string.IsNullOrWhiteSpace(path)) return None;
            if (!Enum.TryParse<StretchMode>(stretch, ignoreCase: true, out _))
                stretch = "Uniform";
            if (stretch is "" or "Auto")
                stretch = "Auto";

            return (path, opacity, stretch);
        }
        catch
        {
            return None;
        }
    }

    /// <summary>
    /// Persists a background-image preference. Path may be empty to clear.
    /// Opacity is clamped to 0-100; an unrecognized Stretch falls back to
    /// Uniform.
    /// </summary>
    public static void Save(string path, int opacity, string stretch)
    {
        try
        {
            var payload = new
            {
                Path = string.IsNullOrWhiteSpace(path) ? string.Empty : path,
                Opacity = (int)Math.Clamp(opacity, 0, 100),
                Stretch = Enum.TryParse<StretchMode>(stretch, ignoreCase: true, out var m)
                    ? m.ToString()
                    : "Uniform",
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false,
            });

            string dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, json);
        }
        catch { /* the image still applies for this session */ }
    }

    /// <summary>Removes the stored preference (no background image).</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(StorePath)) File.Delete(StorePath);
        }
        catch { }
    }
}
