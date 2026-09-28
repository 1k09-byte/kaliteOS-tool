// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary.
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute,
// sublicense, or sell copies of this software, in any form, whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.IO;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Loads the outcome-category mapping. The shipped JSON under Assets is a seed; the
/// copy in %LOCALAPPDATA% is the one the user edits, so changing the grouping never
/// needs a rebuild. This class is deliberately thin - all parsing lives in
/// <see cref="NvidiaSettingCategoryMap.Parse"/> so it can be unit tested.
/// </summary>
public sealed class NvidiaSimpleSettingsConfig
{
    private const string SeedFileName = "setting-categories.json";

    private readonly string _userConfigPath;
    private readonly string _seedPath;

    public NvidiaSimpleSettingsConfig(string? userConfigPath = null, string? seedPath = null)
    {
        _userConfigPath = userConfigPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "Nvidia", SeedFileName);
        _seedPath = seedPath ?? Path.Combine(
            AppContext.BaseDirectory, "Assets", "Nvidia", SeedFileName);
    }

    public string UserConfigPath => _userConfigPath;

    /// <summary>
    /// Returns the user's mapping, seeding it from the shipped file.
    ///
    /// The seed is refreshed when the on-disk copy was written for an older set of
    /// settings, detected by its schema version. Without that, a machine that ran an
    /// earlier build keeps a mapping for settings that no longer exist and every newly
    /// added setting silently falls into "Other" - which looks like the app is broken
    /// rather than out of date. A copy already at the current version is never
    /// touched, so hand edits made against this build survive.
    ///
    /// Any failure (missing file, unreadable file, bad JSON) falls back to the empty
    /// map, which categorizes everything as "Other" - degraded, never broken.
    /// </summary>
    public NvidiaSettingCategoryMap Load()
    {
        try
        {
            if (ShouldSeed())
            {
                string? dir = Path.GetDirectoryName(_userConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(_seedPath, _userConfigPath, overwrite: true);
            }
            if (File.Exists(_userConfigPath))
                return NvidiaSettingCategoryMap.Parse(File.ReadAllText(_userConfigPath));
        }
        catch (IOException) { /* fall through to empty */ }
        catch (UnauthorizedAccessException) { /* fall through to empty */ }
        return NvidiaSettingCategoryMap.Empty;
    }

    private bool ShouldSeed()
    {
        if (!File.Exists(_seedPath)) return false;
        if (!File.Exists(_userConfigPath)) return true;
        try
        {
            int onDisk = NvidiaSettingCategoryMap.ReadSchemaVersion(File.ReadAllText(_userConfigPath));
            return onDisk < NvidiaSettingCategoryMap.CurrentSchemaVersion;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
