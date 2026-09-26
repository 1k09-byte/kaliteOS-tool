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
using System.Collections.Generic;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>Built-in Essentials bundle, seeded once on first run (never re-seeded
/// after the user deletes it). All IDs are winget package IDs.</summary>
public static class EssentialsBundle
{
    public const string BundleName = "Essentials";
    public const string MarkerFile = ".essentials.v1.seeded";

    public static List<BundleItem> Items => new()
    {
        new BundleItem { PackageId = "Mullvad.MullvadBrowser", SourceId = "winget", DisplayName = "Mullvad Browser" },
        new BundleItem { PackageId = "voidtools.Everything", SourceId = "winget", DisplayName = "Everything" },
        new BundleItem { PackageId = "7zip.7zip", SourceId = "winget", DisplayName = "7-Zip" },
        new BundleItem { PackageId = "M2Team.NanaZip", SourceId = "winget", DisplayName = "NanaZip" },
        new BundleItem { PackageId = "Microsoft.Sysinternals.ProcessExplorer", SourceId = "winget", DisplayName = "Process Explorer" },
        new BundleItem { PackageId = "Rufus.Rufus", SourceId = "winget", DisplayName = "Rufus" },
        new BundleItem { PackageId = "Ventoy.Ventoy", SourceId = "winget", DisplayName = "Ventoy" },
        new BundleItem { PackageId = "Microsoft.VisualStudioCode", SourceId = "winget", DisplayName = "Visual Studio Code" },
        new BundleItem { PackageId = "Microsoft.VisualStudio.2022.Community", SourceId = "winget", DisplayName = "Visual Studio 2022 Community" },
        new BundleItem { PackageId = "Git.Git", SourceId = "winget", DisplayName = "Git" },
        new BundleItem { PackageId = "Python.Python.3", SourceId = "winget", DisplayName = "Python 3" },
        new BundleItem { PackageId = "OBSProject.OBSStudio", SourceId = "winget", DisplayName = "OBS Studio" },
        new BundleItem { PackageId = "MedalB.V.Medal", SourceId = "winget", DisplayName = "Medal" },
        new BundleItem { PackageId = "Bitwarden.Bitwarden", SourceId = "winget", DisplayName = "Bitwarden" },
        new BundleItem { PackageId = "WiresharkFoundation.Wireshark", SourceId = "winget", DisplayName = "Wireshark" },
        new BundleItem { PackageId = "AnyDeskSoftwareGmbH.AnyDesk", SourceId = "winget", DisplayName = "AnyDesk" },
        new BundleItem { PackageId = "RustDesk.RustDesk", SourceId = "winget", DisplayName = "RustDesk" },
        new BundleItem { PackageId = "Froststrap.Froststrap", SourceId = "winget", DisplayName = "Froststrap" },
    };
}
