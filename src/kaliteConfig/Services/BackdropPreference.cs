// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.IO;

namespace kaliteConfig.Services;

/// <summary>
/// Records whether the user has ever picked a backdrop material in Settings.
///
/// The material itself is persisted by DevWinUI, which gives no way to tell "the user
/// chose None" apart from "nothing has been chosen yet" — both read back as None. Without
/// this flag, applying the Mica Alt default would undo a deliberate "None" on every
/// launch, so the choice is tracked here instead.
/// </summary>
internal static class BackdropPreference
{
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "material-chosen.marker");

    /// <summary>True once the user has picked a material; until then the default applies.</summary>
    public static bool MaterialChosen
    {
        get
        {
            try { return File.Exists(MarkerPath); }
            // If the marker cannot be read, assume the user has chosen something: better to
            // leave the material alone than to overwrite a choice on every launch.
            catch { return true; }
        }
        set
        {
            try
            {
                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                    File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
                }
                else if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }
            }
            catch { /* the marker is a convenience, never a reason to fail startup */ }
        }
    }
}
