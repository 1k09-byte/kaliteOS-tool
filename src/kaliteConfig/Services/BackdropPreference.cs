// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.IO;

namespace kaliteConfig.Services;

/// <summary>
/// Records whether the user has ever picked a Material in Settings.
///
/// DevWinUI persists the material itself, but it cannot distinguish "the user
/// deliberately chose None" from "nothing has been chosen yet" - both read back
/// as None. Worse, its first launch writes the app's configured fallback to disk,
/// so the old Mica Alt default silently becomes indistinguishable from a real
/// user choice. This flag lets the app apply its intended default (a solid black
/// window surface) only while the user has stayed out of Appearance, and get out
/// of the way afterwards.
///
/// The App theme has no flag: its default is "Use system setting", so there is
/// nothing for the app to force.
/// </summary>
internal static class BackdropPreference
{
    private static string MarkerPath(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", name);

    private static bool Chosen(string marker)
    {
        try { return File.Exists(MarkerPath(marker)); }
        // If the marker cannot be read, assume the user has chosen something: better to
        // leave the appearance alone than to overwrite a choice on every launch.
        catch { return true; }
    }

    private static void SetChosen(string marker, bool value)
    {
        try
        {
            string path = MarkerPath(marker);
            if (value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* the marker is a convenience, never a reason to fail startup */ }
    }

    /// <summary>True once the user has picked a Material; until then the default applies.</summary>
    public static bool MaterialChosen
    {
        get => Chosen("material-chosen.marker");
        set => SetChosen("material-chosen.marker", value);
    }
}