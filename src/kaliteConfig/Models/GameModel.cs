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
using System.Text.Json.Serialization;

namespace kaliteConfig.Models;

public enum GameLauncher
{
    Steam,
    Epic,
    Gog,
    Riot,
    Roblox,
    Ubisoft,
    BattleNet,
    Manual
}

public sealed class GameModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public GameLauncher Launcher { get; set; } = GameLauncher.Manual;
    public string CoverImageUrl { get; set; } = string.Empty;
    public string HeroImageUrl { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;
    public List<string> Genres { get; set; } = new();
    public DateTime? InstalledUtc { get; set; }

    [JsonIgnore]
    public bool IsManual => Launcher == GameLauncher.Manual;

    [JsonIgnore]
    public string LauncherIconPath => Launcher switch
    {
        GameLauncher.Steam => "ms-appx:///Assets/steam-logo.png",
        GameLauncher.Epic => "ms-appx:///Assets/epic-logo.png",
        GameLauncher.Ubisoft => "ms-appx:///Assets/ubisoft-logo.png",
        GameLauncher.Riot => "ms-appx:///Assets/riot-logo.svg",
        GameLauncher.Roblox => "ms-appx:///Assets/roblox-logo.ico",
        _ => string.Empty
    };

    [JsonIgnore]
    public bool HasLauncherIcon => !string.IsNullOrEmpty(LauncherIconPath);

    [JsonIgnore]
    public string LauncherGlyph => Launcher switch
    {
        GameLauncher.Steam => "\uE7FC",
        GameLauncher.Epic => "\uE7FC",
        GameLauncher.Roblox => "\uE7FC",
        GameLauncher.Gog => "\uE7FC",
        GameLauncher.Ubisoft => "\uE713",
        GameLauncher.BattleNet => "\uE7FC",
        _ => "\uE8FB"
    };

    [JsonIgnore]
    public string LauncherLabel => Launcher switch
    {
        GameLauncher.Steam => "Steam",
        GameLauncher.Epic => "Epic Games",
        GameLauncher.Gog => "GOG",
        GameLauncher.Riot => "Riot",
        GameLauncher.Roblox => "Roblox",
        GameLauncher.Ubisoft => "Ubisoft Connect",
        GameLauncher.BattleNet => "Battle.net",
        _ => "Manual"
    };
}
