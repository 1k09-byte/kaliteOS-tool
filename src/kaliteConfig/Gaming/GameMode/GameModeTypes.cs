using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kaliteConfig.Gaming.GameMode;

public enum GameModeState { Idle, Pending, Active, Releasing }

public enum BackgroundAction { None, LowerPriority, Efficiency, ConfineCores, Suspend }

public enum BackgroundCategory { Updaters, Launchers, Overlays, Browsers, CloudSync, Chat, UserAdded, CaptureTools }

public enum GameModePreset { Light, Balanced, Aggressive, Custom }

public enum GameModeOutcome
{
    Applicable,
    AppliedVerified,
    AppliedUnverified,
    Refused,
    SkippedProtected,
    SkippedSafety,
    Downgraded,
    AlreadyInState,
    RestoredVerified,
    RestoreFailed,
}

public sealed record BackgroundCatalogEntry(
    string Id,
    string DisplayName,
    BackgroundCategory Category,
    string[] ImageNames,
    BackgroundAction DefaultAction,
    bool SuspendEligible,
    string Note);

public sealed record BackgroundDecision(
    int Pid,
    long CreationTicks,
    string Name,
    string? ImagePath,
    BackgroundCategory Category,
    string CatalogId,
    BackgroundAction Requested,
    BackgroundAction Planned,
    GameModeOutcome Outcome,
    string Message);

public sealed record GameModeAffectedRow(
    string Name,
    int Pid,
    string Category,
    string Action,
    string State,
    string Message,
    long CreationTicks);

public sealed record GameModeRestoreSummary(int Restored, int Failed);

public sealed record GameModeEditResult(bool Success, string Message)
{
    public static GameModeEditResult Ok(string message) => new(true, message);
    public static GameModeEditResult Fail(string message) => new(false, message);
}

public sealed record GameModeLogRow(string Time, string Process, string Action, string Result, string Message);

public sealed record GameModeGameEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool MatchByPath { get; set; }
    public GameModePreset Preset { get; set; } = GameModePreset.Balanced;
}

public sealed class GameModeSettings
{
    public bool Enabled { get; set; }
    public int DebounceMs { get; set; } = 750;
    public GameModePreset Preset { get; set; } = GameModePreset.Balanced;
    public bool SuspendEnabled { get; set; }
    public List<GameModeGameEntry> Games { get; set; } = new();
    public List<string> NeverTouch { get; set; } = new();
    public Dictionary<string, BackgroundAction> Actions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public int ClampedDebounce => Math.Clamp(DebounceMs, 250, 5000);
}

public static class BackgroundCatalog
{
    public static readonly BackgroundCatalogEntry[] Entries =
    {
        new("steam", "Steam", BackgroundCategory.Launchers, new[] { "steam.exe", "steamwebhelper.exe", "steamservice.exe" }, BackgroundAction.Efficiency, true, "Client and web helper"),
        new("epic", "Epic Games Launcher", BackgroundCategory.Launchers, new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe" }, BackgroundAction.Efficiency, true, "Launcher and updater"),
        new("battlenet", "Battle.net", BackgroundCategory.Launchers, new[] { "Battle.net.exe", "Agent.exe" }, BackgroundAction.Efficiency, true, "Client and background agent"),
        new("ubisoft", "Ubisoft Connect", BackgroundCategory.Launchers, new[] { "UbisoftConnect.exe", "upc.exe" }, BackgroundAction.Efficiency, true, "Client and updater"),
        new("eaapp", "EA app", BackgroundCategory.Launchers, new[] { "EADesktop.exe", "EABackgroundService.exe" }, BackgroundAction.Efficiency, true, "Client and background service"),
        new("gog", "GOG Galaxy", BackgroundCategory.Launchers, new[] { "GalaxyClient.exe", "GalaxyClientHelper.exe" }, BackgroundAction.Efficiency, true, "Client and helper"),
        new("riot", "Riot Client", BackgroundCategory.Launchers, new[] { "RiotClientServices.exe", "RiotClientUx.exe" }, BackgroundAction.Efficiency, false, "Client; anti-cheat is protected"),
        new("xboxapp", "Xbox app", BackgroundCategory.Launchers, new[] { "XboxPcApp.exe", "GameBar.exe", "GameBarFT.exe" }, BackgroundAction.Efficiency, false, "Xbox app and Game Bar"),
        new("updaters", "Windows updater", BackgroundCategory.Updaters, new[] { "TiWorker.exe", "TrustedInstaller.exe", "MoUsoCoreWorker.exe", "usoclient.exe" }, BackgroundAction.None, false, "System servicing, protected"),
        new("nvidia", "NVIDIA helper", BackgroundCategory.Updaters, new[] { "NVDisplay.Container.exe", "nvcontainer.exe" }, BackgroundAction.None, false, "Display driver container, protected"),
        new("overwolf", "Overwolf", BackgroundCategory.Overlays, new[] { "Overwolf.exe", "OverwolfBrowser.exe", "OverwolfHelper.exe" }, BackgroundAction.Efficiency, false, "Overlay host; throttled only"),
        new("discord", "Discord", BackgroundCategory.Chat, new[] { "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe" }, BackgroundAction.Efficiency, true, "Chat and overlay"),
        new("teams", "Microsoft Teams", BackgroundCategory.Chat, new[] { "ms-teams.exe", "Teams.exe" }, BackgroundAction.Efficiency, true, "Chat"),
        new("slack", "Slack", BackgroundCategory.Chat, new[] { "slack.exe" }, BackgroundAction.Efficiency, true, "Chat"),
        new("telegram", "Telegram", BackgroundCategory.Chat, new[] { "Telegram.exe" }, BackgroundAction.Efficiency, true, "Chat"),
        new("whatsapp", "WhatsApp", BackgroundCategory.Chat, new[] { "WhatsApp.exe" }, BackgroundAction.Efficiency, true, "Chat"),
        new("zoom", "Zoom", BackgroundCategory.Chat, new[] { "Zoom.exe" }, BackgroundAction.Efficiency, false, "Voice and video meetings"),
        new("chrome", "Google Chrome", BackgroundCategory.Browsers, new[] { "chrome.exe" }, BackgroundAction.Efficiency, true, "Browser"),
        new("edge", "Microsoft Edge", BackgroundCategory.Browsers, new[] { "msedge.exe" }, BackgroundAction.Efficiency, true, "Browser"),
        new("firefox", "Mozilla Firefox", BackgroundCategory.Browsers, new[] { "firefox.exe" }, BackgroundAction.Efficiency, true, "Browser"),
        new("brave", "Brave", BackgroundCategory.Browsers, new[] { "brave.exe" }, BackgroundAction.Efficiency, true, "Browser"),
        new("onedrive", "OneDrive", BackgroundCategory.CloudSync, new[] { "OneDrive.exe", "FileCoAuth.exe" }, BackgroundAction.Efficiency, true, "Cloud sync"),
        new("dropbox", "Dropbox", BackgroundCategory.CloudSync, new[] { "Dropbox.exe" }, BackgroundAction.Efficiency, true, "Cloud sync"),
        new("googledrive", "Google Drive", BackgroundCategory.CloudSync, new[] { "GoogleDriveFS.exe" }, BackgroundAction.Efficiency, true, "Cloud sync"),
        new("spotify", "Spotify", BackgroundCategory.Chat, new[] { "Spotify.exe" }, BackgroundAction.Efficiency, true, "Music, may stop playback if suspended"),
        new("obs", "OBS Studio", BackgroundCategory.CaptureTools, new[] { "obs64.exe", "obs32.exe" }, BackgroundAction.LowerPriority, false, "Capture tool; never suspended"),
        new("nvidiaoverlay", "NVIDIA overlay", BackgroundCategory.CaptureTools, new[] { "NVIDIA Share.exe", "NVIDIA Overlay.exe" }, BackgroundAction.LowerPriority, false, "Overlay host; never suspended"),
        new("medal", "Medal", BackgroundCategory.CaptureTools, new[] { "Medal.exe", "MedalEncoder.exe" }, BackgroundAction.LowerPriority, false, "Capture tool; never suspended"),
        new("steelseries", "SteelSeries GG", BackgroundCategory.CaptureTools, new[] { "SteelSeriesGG.exe", "SteelSeriesEngine.exe" }, BackgroundAction.None, false, "Peripheral software; needs your decision"),
        new("razer", "Razer Synapse", BackgroundCategory.CaptureTools, new[] { "RazerSynapse.exe", "RazerCentral.exe" }, BackgroundAction.None, false, "Peripheral software; needs your decision"),
        new("logi", "Logitech G Hub", BackgroundCategory.CaptureTools, new[] { "lghub.exe", "lghub_agent.exe" }, BackgroundAction.None, false, "Peripheral software; needs your decision"),
    };

    public static readonly string[] AntiCheatNames =
    {
        "EasyAntiCheat.exe",
        "EasyAntiCheat_EOS.exe",
        "EasyAntiCheat_Setup.exe",
        "BEService.exe",
        "BEService_x64.exe",
        "BEClient_x64.exe",
        "BESGame.exe",
        "vgc.exe",
        "vgtray.exe",
        "FACEITClient.exe",
        "FACEITService.exe",
        "PnkBstrA.exe",
        "PnkBstrB.exe",
        "GameGuard.des",
        "nProtect GameGuard.exe",
        "XIGNCODE3.exe",
        "EAAntiCheat.exe",
        "EAAntiCheat.GameService.exe",
        "EABackgroundService.exe",
    };

    public static readonly string[] ShellFamily =
    {
        "explorer.exe",
        "dwm.exe",
        "sihost.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "SearchHost.exe",
        "SearchApp.exe",
        "TextInputHost.exe",
        "ctfmon.exe",
        "RuntimeBroker.exe",
        "ApplicationFrameHost.exe",
        "fontdrvhost.exe",
        "audiodg.exe",
        "MsMpEng.exe",
        "NisSrv.exe",
        "SecurityHealthService.exe",
        "SecurityHealthSystray.exe",
        "smartscreen.exe",
        "LockApp.exe",
        "LogonUI.exe",
        "taskhostw.exe",
        "WUDFHost.exe",
        "SystemSettings.exe",
        "backgroundTaskHost.exe",
        "dllhost.exe",
        "WMIC.exe",
        "TrustedInstaller.exe",
        "TiWorker.exe",
        "MoUsoCoreWorker.exe",
        "usoclient.exe",
        "MsiExec.exe",
        "msiexec.exe",
    };

    public static readonly string[] IgnoredForegroundClasses =
    {
        "TaskSwitcherWnd",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",
        "Progman",
        "WorkerW",
    };

    public static BackgroundCatalogEntry? FindByImage(string imageName)
    {
        foreach (var entry in Entries)
            foreach (var name in entry.ImageNames)
                if (name.Equals(imageName, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    public static string Fetch(BackgroundCategory category) => category switch
    {
        BackgroundCategory.CloudSync => "Cloud sync",
        BackgroundCategory.UserAdded => "User added",
        BackgroundCategory.CaptureTools => "Capture tools",
        _ => category.ToString(),
    };
}

public sealed class GameModeSettingsStore
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "game-mode");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string JournalPath => Path.Combine(RootDirectory, "journal.json");
    public static string LogPath => Path.Combine(RootDirectory, "activity.log");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public GameModeSettings Load()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            if (!File.Exists(SettingsPath)) return new GameModeSettings();
            var text = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<GameModeSettings>(text, Options);
            return loaded ?? new GameModeSettings();
        }
        catch
        {
            return new GameModeSettings();
        }
    }

    public void Save(GameModeSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SettingsPath, true);
        }
        catch
        {
        }
    }
}
