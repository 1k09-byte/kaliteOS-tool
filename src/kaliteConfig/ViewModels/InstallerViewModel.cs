using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels
{
    public partial class InstallerViewModel : ObservableObject
    {
        private readonly IInstallerService _installerService;
        private CancellationTokenSource? _cts;

        public ObservableCollection<BrowserInstallItem> Browsers { get; } = new();
        public ObservableCollection<BrowserInstallItem> GameLaunchers { get; } = new();
        public ObservableCollection<BrowserInstallItem> SocialApps { get; } = new();

        /// <summary>Portable sysinternals-style tools (no installer, just an archive payload).</summary>
        public ObservableCollection<BrowserInstallItem> Utilities { get; } = new();

        public ObservableCollection<BrowserInstallItem> VisibleUtilities { get; } = new();

        public ObservableCollection<BrowserInstallItem> VisibleBrowsers { get; } = new();
        public ObservableCollection<BrowserInstallItem> VisibleGameLaunchers { get; } = new();
        public ObservableCollection<BrowserInstallItem> VisibleSocialApps { get; } = new();

        // Installed items are hidden on the Apps page by default: it stays a
        // pure install page. Flip ShowInstalled to reveal them (with uninstall).
        [ObservableProperty]
        public partial bool ShowInstalled { get; set; } = false;

        /// <summary>True when every section is empty - drives the empty-state message.</summary>
        [ObservableProperty]
        public partial bool NothingToInstall { get; set; } = false;

        partial void OnShowInstalledChanged(bool value)
        {
            UpdateVisibilityFilters();
        }

        private void UpdateVisibilityFilters()
        {
            // Gallery reference: ItemsRepeater bound to Visible* collections. Installed items
            // (AlreadyInstalled + freshly Installed) are hidden when ShowInstalled=false.
            // UpdateVisibilityFilters() is re-run after every install/uninstall/cancel so the
            // card disappears right after install and reappears after uninstall.
            VisibleBrowsers.Clear();
            foreach (var b in Browsers)
            {
                b.IsVisible = ShowInstalled || (b.Status != BrowserInstallStatus.AlreadyInstalled && b.Status != BrowserInstallStatus.Installed);
                if (b.IsVisible) VisibleBrowsers.Add(b);
            }

            VisibleGameLaunchers.Clear();
            foreach (var gl in GameLaunchers)
            {
                gl.IsVisible = ShowInstalled || (gl.Status != BrowserInstallStatus.AlreadyInstalled && gl.Status != BrowserInstallStatus.Installed);
                if (gl.IsVisible) VisibleGameLaunchers.Add(gl);
            }

            VisibleSocialApps.Clear();
            foreach (var sa in SocialApps)
            {
                sa.IsVisible = ShowInstalled || (sa.Status != BrowserInstallStatus.AlreadyInstalled && sa.Status != BrowserInstallStatus.Installed);
                if (sa.IsVisible) VisibleSocialApps.Add(sa);
            }

            VisibleUtilities.Clear();
            foreach (var util in Utilities)
            {
                util.IsVisible = ShowInstalled || (util.Status != BrowserInstallStatus.AlreadyInstalled && util.Status != BrowserInstallStatus.Installed);
                if (util.IsVisible) VisibleUtilities.Add(util);
            }

            NothingToInstall = VisibleBrowsers.Count == 0 && VisibleGameLaunchers.Count == 0
                && VisibleSocialApps.Count == 0 && VisibleUtilities.Count == 0;
        }

        [ObservableProperty]
        public partial BrowserInstallItem? SelectedBrowser { get; set; }

        [ObservableProperty]
        public partial bool IsInstalling { get; set; }

        [ObservableProperty]
        public partial string InstallStatusText { get; set; } = string.Empty;

        public InstallerViewModel(IInstallerService installerService)
        {
            _installerService = installerService;

            var zenBrowser = new BrowserInstallItem
            {
                Name = "Zen Browser",
                ImagePath = "ms-appx:///Assets/zen-logo.png",
                DownloadUrl = "https://github.com/zen-browser/desktop/releases/latest/download/zen.installer.exe",
                SilentInstallArgs = "/S",
                InstallerFileName = "zen.installer.exe",
                InstalledCheckPath = @"%PROGRAMFILES%\Zen Browser\zen.exe;%LOCALAPPDATA%\Programs\Zen Browser\zen.exe;%LOCALAPPDATA%\zen\zen.exe",
                Description = "Zen Browser is a privacy-focused browser built to keep you safe and fast online."
            };

            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "uBlock Origin",
                FirefoxAddonSlug = "ublock-origin",
                Description = "Blocks ads and trackers across every site you visit."
            });
            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Privacy Badger",
                FirefoxAddonSlug = "privacy-badger17",
                Description = "Automatically learns to block hidden trackers as you browse, made by the EFF."
            });
            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Location Guard",
                FirefoxAddonSlug = "location-guard",
                Description = "Hides your precise geographic location from websites by adding noise to it."
            });
            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "I Still Don't Care About Cookies",
                FirefoxAddonSlug = "istilldontcareaboutcookies",
                Description = "Automatically dismisses cookie consent banners on most websites."
            });
            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Decentraleyes",
                FirefoxAddonSlug = "decentraleyes",
                Description = "Protects against tracking through free, centralized content delivery by serving local files."
            });
            zenBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "ClearURLs",
                FirefoxAddonSlug = "clearurls",
                Description = "Automatically removes tracking parameters from URLs."
            });

            var braveBrowser = new BrowserInstallItem
            {
                Name = "Brave Browser",
                ImagePath = "ms-appx:///Assets/brave-logo.png",
                DownloadUrl = "https://brave-browser-downloads.s3.brave.com/latest/brave_installer-x64.exe",
                SilentInstallArgs = "--silent --install",
                InstallerFileName = "brave_installer-x64.exe",
                InstalledCheckPath = @"%PROGRAMFILES%\BraveSoftware\Brave-Browser\Application\brave.exe;%PROGRAMFILES(X86)%\BraveSoftware\Brave-Browser\Application\brave.exe;%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe",
                Description = "Brave is a free and open-source web browser developed by Brave Software, Inc. based on the Chromium web browser."
            };

            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "uBlock Origin Lite",
                ChromiumExtensionId = "ddkjiahejlhfcafbddmgiahcphecmpfh",
                Description = "Blocks ads and trackers across every site you visit. (Fallback since MV2 uBlock Origin was removed from CWS)"
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Privacy Badger",
                ChromiumExtensionId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
                Description = "Automatically learns to block hidden trackers. (Blocked by Brave natively / Redundant with Shields)",
                IsAvailable = false,
                IsSelected = false
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Location Guard",
                ChromiumExtensionId = "cfohepagpmnodfdmjliccbbigdkfcgia",
                Description = "Hides your precise geographic location from websites by adding noise to it. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "WebRTC Leak Shield",
                ChromiumExtensionId = "bppamachkoflopbagkdoflbgfjflfnfl",
                Description = "Prevents WebRTC from leaking your real IP address, even behind a VPN."
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "I Still Don't Care About Cookies",
                ChromiumExtensionId = "edibdbjcniadpccecjdfdjjppcpchdlm",
                Description = "Automatically dismisses cookie consent banners on most websites."
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Decentraleyes",
                ChromiumExtensionId = "ldpochfccmkkmhdbclfhpagapcfdljkj",
                Description = "Protects against tracking through free, centralized content delivery by serving local files."
            });
            braveBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "ClearURLs",
                ChromiumExtensionId = "lckanjgmijmafbedllaakclkaicjfmnk",
                Description = "Automatically removes tracking parameters from URLs. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });

            var vivaldiBrowser = new BrowserInstallItem
            {
                Name = "Vivaldi",
                ImagePath = "ms-appx:///Assets/vivaldi-logo.png",
                // The plain "Vivaldi.Installer.exe" alias 404s; but this permalink
                // intelligently redirects to the latest stable x64 build.
                DownloadUrl = "https://vivaldi.com/download/Vivaldi.x64.exe",
                // Verified switches from the installer binary itself
                // (--vivaldi-silent, --vivaldi-mini, --vivaldi-unpack,
                // --system-level). --do-not-launch-chrome is a Chrome-era
                // switch the Vivaldi installer does not implement.
                SilentInstallArgs = "--vivaldi-silent --system-level",
                InstallerFileName = "vivaldi_installer.exe",
                InstalledCheckPath = @"%PROGRAMFILES%\Vivaldi\Application\vivaldi.exe;%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe",
                Description = "A fast, highly customizable privacy browser built on Chromium."
            };

            var heliumBrowser = new BrowserInstallItem
            {
                Name = "Helium Browser",
                ImagePath = "ms-appx:///Assets/helium-logo.svg",
                DownloadUrl = "https://github.com/imputnet/helium-windows/releases/download/0.16.5.1/helium_0.16.5.1_x64-installer.exe",
                SilentInstallArgs = "/S",
                InstallerFileName = "helium_installer.exe",
                InstalledCheckPath = @"%LOCALAPPDATA%\imput\Helium\Application\chrome.exe;%LOCALAPPDATA%\imput\Helium\Application\helium.exe;%LOCALAPPDATA%\Programs\Helium\Helium.exe;%LOCALAPPDATA%\Programs\Helium\chrome.exe;%PROGRAMFILES%\imput\Helium\Application\chrome.exe;%PROGRAMFILES%\Helium\Application\chrome.exe;%PROGRAMFILES%\Helium\Helium.exe;%PROGRAMFILES(X86)%\imput\Helium\Application\chrome.exe;%PROGRAMFILES(X86)%\Helium\Helium.exe;%LOCALAPPDATA%\Helium\Application\helium.exe",
                Description = "An open-source, bloat-free browser based on ungoogled-chromium designed purely for maximum privacy."
            };

            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "uBlock Origin Lite",
                ChromiumExtensionId = "ddkjiahejlhfcafbddmgiahcphecmpfh",
                Description = "Blocks ads and trackers across every site you visit. (Fallback since MV2 uBlock Origin was removed from CWS)"
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Privacy Badger",
                ChromiumExtensionId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
                Description = "Automatically learns to block hidden trackers as you browse, made by the EFF."
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Location Guard",
                ChromiumExtensionId = "cfohepagpmnodfdmjliccbbigdkfcgia",
                Description = "Hides your precise geographic location from websites by adding noise to it. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "WebRTC Leak Shield",
                ChromiumExtensionId = "bppamachkoflopbagkdoflbgfjflfnfl",
                Description = "Prevents WebRTC from leaking your real IP address, even behind a VPN."
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "I Still Don't Care About Cookies",
                ChromiumExtensionId = "edibdbjcniadpccecjdfdjjppcpchdlm",
                Description = "Automatically dismisses cookie consent banners on most websites."
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Decentraleyes",
                ChromiumExtensionId = "ldpochfccmkkmhdbclfhpagapcfdljkj",
                Description = "Protects against tracking through free, centralized content delivery by serving local files."
            });
            heliumBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "ClearURLs",
                ChromiumExtensionId = "lckanjgmijmafbedllaakclkaicjfmnk",
                Description = "Automatically removes tracking parameters from URLs. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });


            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "uBlock Origin Lite",
                ChromiumExtensionId = "ddkjiahejlhfcafbddmgiahcphecmpfh",
                Description = "Blocks ads and trackers across every site you visit. (Fallback since MV2 uBlock Origin was removed from CWS)"
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Privacy Badger",
                ChromiumExtensionId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
                Description = "Automatically learns to block hidden trackers as you browse, made by the EFF."
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Location Guard",
                ChromiumExtensionId = "cfohepagpmnodfdmjliccbbigdkfcgia",
                Description = "Hides your precise geographic location from websites by adding noise to it. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "WebRTC Leak Shield",
                ChromiumExtensionId = "bppamachkoflopbagkdoflbgfjflfnfl",
                Description = "Prevents WebRTC from leaking your real IP address, even behind a VPN."
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "I Still Don't Care About Cookies",
                ChromiumExtensionId = "edibdbjcniadpccecjdfdjjppcpchdlm",
                Description = "Automatically dismisses cookie consent banners on most websites."
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Decentraleyes",
                ChromiumExtensionId = "ldpochfccmkkmhdbclfhpagapcfdljkj",
                Description = "Protects against tracking through free, centralized content delivery by serving local files."
            });
            vivaldiBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "ClearURLs",
                ChromiumExtensionId = "lckanjgmijmafbedllaakclkaicjfmnk",
                Description = "Automatically removes tracking parameters from URLs. (Unavailable - MV2 delisted from CWS)",
                IsAvailable = false,
                IsSelected = false
            });

            var epicLauncher = new BrowserInstallItem
            {
                Name = "Epic Games Launcher",
                ImagePath = "ms-appx:///Assets/epic-logo.png",
                DownloadUrl = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi",
                SilentInstallArgs = "/qn /norestart",
                InstallerFileName = "epic_installer.msi",
                InstalledCheckPath = @"%PROGRAMFILES(X86)%\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe;%PROGRAMFILES(X86)%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                Description = "The official Epic Games storefront, featuring Unreal Engine titles, exclusive games, and free weekly releases."
            };

            var eaApp = new BrowserInstallItem
            {
                Name = "EA app",
                ImagePath = "ms-appx:///Assets/ea-logo.png",
                DownloadUrl = "https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller-13.783.0.6296-15340466.exe",
                SilentInstallArgs = "/quiet",
                InstallerFileName = "ea_installer.exe",
                InstalledCheckPath = @"%PROGRAMFILES%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
                Description = "The official Electronic Arts ecosystem, built for downloading and playing Battlefield, The Sims, and EA Sports titles."
            };

            var ubisoftConnect = new BrowserInstallItem
            {
                Name = "Ubisoft Connect",
                ImagePath = "ms-appx:///Assets/ubisoft-logo.png",
                DownloadUrl = "https://ubistatic3-a.akamaihd.net/orbit/launcher_installer/UbisoftConnectInstaller.exe",
                SilentInstallArgs = "/S",
                InstallerFileName = "ubisoft_installer.exe",
                InstalledCheckPath = @"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher\upc.exe",
                Description = "The central application for managing Ubisoft games, enabling cross-progression, rewards, and deep multiplayer integration."
            };

            var minecraftLauncher = new BrowserInstallItem
            {
                Name = "Minecraft Launcher",
                ImagePath = "ms-appx:///Assets/minecraft-logo.png",
                DownloadUrl = "https://launcher.mojang.com/download/MinecraftInstaller.msi",
                SilentInstallArgs = "/qn",
                InstallerFileName = "minecraft_installer.msi",
                InstalledCheckPath = @"%PROGRAMFILES(X86)%\Minecraft Launcher\MinecraftLauncher.exe",
                Description = "The unified launcher for Minecraft: Java Edition, Bedrock, and Dungeons, providing quick access to all block-building adventures."
            };

            var steamLauncher = new BrowserInstallItem
            {
                Name = "Steam",
                ImagePath = "ms-appx:///Assets/steam-logo.png",
                DownloadUrl = "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
                SilentInstallArgs = "/S",
                InstallerFileName = "steam_installer.exe",
                InstalledCheckPath = @"%PROGRAMFILES(X86)%\Steam\steam.exe",
                Description = "The ultimate gaming platform and community, featuring massive game libraries, workshop mods, and cloud saves operated by Valve."
            };

            var riotLauncher = new BrowserInstallItem
            {
                Name = "Riot Client",
                ImagePath = "ms-appx:///Assets/riot-logo.svg",
                DownloadUrl = "https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.na.exe",
                SilentInstallArgs = "--mode unattended",
                InstallerFileName = "riot_installer.exe",
                InstalledCheckPath = @"%PROGRAMDATA%\Riot Games\Riot Client\RiotClientServices.exe;C:\Riot Games\Riot Client\RiotClientServices.exe",
                Description = "The unified platform for launching all Riot Games titles including League of Legends and VALORANT."
            };

            var discordLauncher = new BrowserInstallItem
            {
                Name = "Discord",
                ImagePath = "ms-appx:///Assets/discord-logo.svg",
                DownloadUrl = "https://discord.com/api/download?platform=win",
                SilentInstallArgs = "-s",
                InstallerFileName = "discord_installer.exe",
                InstalledCheckPath = @"%LOCALAPPDATA%\Discord\*\Discord.exe",
                Description = "The go-to voice, video, and text communication service used by gamers and communities worldwide."
            };

            var telegramApp = new BrowserInstallItem
            {
                Name = "Telegram Desktop",
                ImagePath = "ms-appx:///Assets/telegram-logo.png",
                DownloadUrl = "https://telegram.org/dl/desktop/win64",
                SilentInstallArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                InstallerFileName = "telegram_installer.exe",
                InstalledCheckPath = @"%APPDATA%\Telegram Desktop\Telegram.exe",
                Description = "A fast, hyper-secure messaging app with seamless cloud syncing across all your devices."
            };

            var whatsappApp = new BrowserInstallItem
            {
                Name = "WhatsApp",
                ImagePath = "ms-appx:///Assets/whatsapp-logo.svg",
                DownloadUrl = "https://web.whatsapp.com/desktop/windows/release/x64/WhatsAppSetup.exe",
                SilentInstallArgs = "/S",
                InstallerFileName = "whatsapp_installer.exe",
                InstalledCheckPath = @"%LOCALAPPDATA%\WhatsApp\WhatsApp.exe",
                Description = "Seamless cross-platform messaging and calling, keeping you connected straight from your desktop."
            };

            Browsers.Add(zenBrowser);
            Browsers.Add(braveBrowser);
            Browsers.Add(vivaldiBrowser);
            Browsers.Add(heliumBrowser);
            var mullvadBrowser = new BrowserInstallItem
            {
                Name = "Mullvad Browser",
                ImagePath = "ms-appx:///Assets/mullvad-logo.png",
                WingetId = "Mullvad.MullvadBrowser",
                InstalledCheckPath = @"%PROGRAMFILES%\Mullvad Browser\mullvadbrowser.exe",
                Description = "Privacy-focused browser from Mullvad and the Tor Project, built to resist fingerprinting."
            };

            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "uBlock Origin",
                FirefoxAddonSlug = "ublock-origin",
                Description = "Blocks ads and trackers across every site you visit."
            });
            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Privacy Badger",
                FirefoxAddonSlug = "privacy-badger17",
                Description = "Automatically learns to block hidden trackers, made by the EFF."
            });
            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Location Guard",
                FirefoxAddonSlug = "location-guard",
                Description = "Hides your precise geographic location from websites by adding noise to it."
            });
            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "I Still Don't Care About Cookies",
                FirefoxAddonSlug = "istilldontcareaboutcookies",
                Description = "Automatically dismisses cookie consent banners on most websites."
            });
            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "Decentraleyes",
                FirefoxAddonSlug = "decentraleyes",
                Description = "Protects against tracking through free, centralized content delivery by serving local files."
            });
            mullvadBrowser.Extensions.Add(new ExtensionItem
            {
                Name = "ClearURLs",
                FirefoxAddonSlug = "clearurls",
                Description = "Automatically removes tracking parameters from URLs."
            });
            Browsers.Add(mullvadBrowser);

            GameLaunchers.Add(epicLauncher);
            GameLaunchers.Add(eaApp);
            GameLaunchers.Add(ubisoftConnect);
            GameLaunchers.Add(minecraftLauncher);
            GameLaunchers.Add(steamLauncher);
            GameLaunchers.Add(riotLauncher);
            GameLaunchers.Add(new BrowserInstallItem
            {
                Name = "Froststrap",
                ImagePath = "ms-appx:///Assets/froststrap-logo.png",
                WingetId = "Froststrap.Froststrap",
                InstalledCheckPath = @"%LOCALAPPDATA%\Froststrap\Froststrap.exe",
                Description = "Fast Roblox bootstrapper focused on performance and customization."
            });
            GameLaunchers.Add(new BrowserInstallItem
            {
                Name = "Medal",
                ImagePath = "ms-appx:///Assets/medal-logo.png",
                WingetId = "MedalB.V.Medal",
                InstalledCheckPath = @"%LOCALAPPDATA%\Medal\Medal.exe",
                Description = "Clip your gameplay without dropping frames, then edit and share it."
            });

            SocialApps.Add(discordLauncher);
            SocialApps.Add(telegramApp);
            SocialApps.Add(whatsappApp);

            Utilities.Add(new BrowserInstallItem
            {
                Name = "Autoruns",
                ImagePath = "ms-appx:///Assets/autoruns-logo.png",
                DownloadUrl = "https://download.sysinternals.com/files/Autoruns.zip",
                SilentInstallArgs = string.Empty,
                InstallerFileName = "Autoruns.zip",
                ToolInstallDir = @"%PROGRAMDATA%\kaliteTools\Autoruns",
                InstalledCheckPath = @"%PROGRAMDATA%\kaliteTools\Autoruns\Autoruns64.exe",
                Description = "Sysinternals utility that shows every autostart location - startup folders, services, drivers, scheduled tasks and more - so nothing launches behind your back."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Everything",
                ImagePath = "ms-appx:///Assets/everything-logo.png",
                WingetId = "voidtools.Everything",
                InstalledCheckPath = @"%PROGRAMFILES%\Everything\Everything.exe",
                Description = "Instant file search across every drive by name."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "7-Zip",
                ImagePath = "ms-appx:///Assets/7zip-logo.png",
                WingetId = "7zip.7zip",
                InstalledCheckPath = @"%PROGRAMFILES%\7-Zip\7z.exe",
                Description = "Free archiver for zip, 7z, rar and everything else."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "NanaZip",
                ImagePath = "ms-appx:///Assets/nanazip-logo.png",
                WingetId = "M2Team.NanaZip",
                InstalledCheckPath = @"%PROGRAMFILES%\NanaZip\NanaZip.exe",
                Description = "Modern 7-Zip fork with Windows 11 shell integration."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Process Explorer",
                ImagePath = "ms-appx:///Assets/procexp-logo.png",
                WingetId = "Microsoft.Sysinternals.ProcessExplorer",
                InstalledCheckPath = string.Empty,
                Description = "Sysinternals task manager on steroids: handles, DLLs and live process trees."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Rufus",
                ImagePath = "ms-appx:///Assets/rufus-logo.png",
                WingetId = "Rufus.Rufus",
                InstalledCheckPath = string.Empty,
                Description = "Create bootable USB drives in seconds."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Ventoy",
                ImagePath = "ms-appx:///Assets/ventoy-logo.png",
                WingetId = "Ventoy.Ventoy",
                InstalledCheckPath = string.Empty,
                Description = "Multiboot USB tool: drop ISOs on a stick and boot any of them."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Visual Studio Code",
                ImagePath = "ms-appx:///Assets/vscode-logo.png",
                WingetId = "Microsoft.VisualStudioCode",
                InstalledCheckPath = @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
                Description = "Lightweight, extensible code editor."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Visual Studio 2026 Community",
                ImagePath = "ms-appx:///Assets/vs-logo.png",
                WingetId = "Microsoft.VisualStudio.Community",
                InstalledCheckPath = @"%PROGRAMFILES%\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
                Description = "Full .NET and C++ IDE, free Community edition."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Git",
                ImagePath = "ms-appx:///Assets/git-logo.png",
                WingetId = "Git.Git",
                InstalledCheckPath = @"%PROGRAMFILES%\Git\bin\git.exe",
                Description = "Distributed version control for everything you build."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Python 3.14",
                ImagePath = "ms-appx:///Assets/python-logo.png",
                WingetId = "Python.Python.3.14",
                InstalledCheckPath = @"%LOCALAPPDATA%\Programs\Python\Python314\python.exe",
                Description = "Latest Python runtime for scripts and tools."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "OBS Studio",
                ImagePath = "ms-appx:///Assets/obs-logo.png",
                WingetId = "OBSProject.OBSStudio",
                InstalledCheckPath = @"%PROGRAMFILES%\obs-studio\bin\64bit\obs64.exe",
                Description = "Free recording and streaming studio."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Bitwarden",
                ImagePath = "ms-appx:///Assets/bitwarden-logo.png",
                WingetId = "Bitwarden.Bitwarden",
                InstalledCheckPath = @"%LOCALAPPDATA%\Programs\Bitwarden\Bitwarden.exe",
                Description = "Open-source password manager with end-to-end encryption."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "Wireshark",
                ImagePath = "ms-appx:///Assets/wireshark-logo.png",
                WingetId = "WiresharkFoundation.Wireshark",
                InstalledCheckPath = @"%PROGRAMFILES%\Wireshark\Wireshark.exe",
                Description = "Deep packet inspection for diagnosing network traffic."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "AnyDesk",
                ImagePath = "ms-appx:///Assets/anydesk-logo.png",
                WingetId = "AnyDeskSoftwareGmbH.AnyDesk",
                InstalledCheckPath = @"%PROGRAMFILES%\AnyDesk\AnyDesk.exe",
                Description = "Fast remote desktop access."
            });
            Utilities.Add(new BrowserInstallItem
            {
                Name = "RustDesk",
                ImagePath = "ms-appx:///Assets/rustdesk-logo.png",
                WingetId = "RustDesk.RustDesk",
                InstalledCheckPath = @"%PROGRAMFILES%\RustDesk\rustdesk.exe",
                Description = "Open-source remote desktop, self-hostable."
            });

            foreach (var browser in Browsers)
            {
                if (_installerService.IsBrowserInstalled(browser))
                {
                    browser.Status = BrowserInstallStatus.AlreadyInstalled;
                }
            }

            foreach (var launcher in GameLaunchers)
            {
                if (_installerService.IsBrowserInstalled(launcher))
                {
                    launcher.Status = BrowserInstallStatus.AlreadyInstalled;
                }
            }

            foreach (var app in SocialApps)
            {
                if (_installerService.IsBrowserInstalled(app))
                {
                    app.Status = BrowserInstallStatus.AlreadyInstalled;
                }
            }

            foreach (var util in Utilities)
            {
                if (_installerService.IsBrowserInstalled(util))
                {
                    util.Status = BrowserInstallStatus.AlreadyInstalled;
                }
            }

            // Sync the initial UI filter state to match the model (Show installed by default – fixed disappearance bug)
            UpdateVisibilityFilters();
            
            // Fix: prepopulate SelectedBrowser so ContentDialog x:Bind doesn't throw NRE on initialization.
            SelectedBrowser = VisibleBrowsers.FirstOrDefault();
        }

        [RelayCommand]
        private async Task InstallBrowserAsync(BrowserInstallItem? item)
        {
            if (item is null) return;
            SelectedBrowser = item;

            _cts = new CancellationTokenSource();

            IsInstalling = true;
            InstallStatusText = "Initializing...";

            var progress = new Progress<BrowserInstallStatus>(status => 
            {
                item.Status = status;
                InstallStatusText = item.StatusText;
                
                if (status == BrowserInstallStatus.Installed || status == BrowserInstallStatus.Failed || status == BrowserInstallStatus.AlreadyInstalled)
                {
                    IsInstalling = false;
                    // Keep gallery visible after install: sync filter so Helium doesn't "disappear" from UI
                    UpdateVisibilityFilters();
                }
            });
            var downloadProgress = new Progress<double>(val => 
            {
                item.DownloadProgress = val;
                InstallStatusText = $"Downloading... ({val:F1}%)";
            });
            var errorProgress = new Progress<string>(error => item.ErrorMessage = error);

            try
            {
                await Task.Run(() => _installerService.InstallBrowserAsync(item, progress, downloadProgress, errorProgress, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                item.ErrorMessage = "Cancelled";
                item.Status = BrowserInstallStatus.Failed;
                UpdateVisibilityFilters();
            }

            IsInstalling = false;
            _cts.Dispose();
            _cts = null;
        }

        [RelayCommand]
        private async Task UninstallBrowserAsync(BrowserInstallItem? item)
        {
            if (item is null) return;
            SelectedBrowser = item;
            
            _cts = new CancellationTokenSource();
            IsInstalling = true;
            InstallStatusText = "Uninstalling...";
            
            try
            {
                await _installerService.UninstallBrowserAsync(item, _cts.Token);
                item.Status = BrowserInstallStatus.NotInstalled;
                InstallStatusText = "";
                UpdateVisibilityFilters();
            }
            catch (Exception ex)
            {
                InstallStatusText = $"Uninstall Failed: {ex.Message}";
            }
            finally
            {
                IsInstalling = false;
                _cts.Dispose();
                _cts = null;
            }
        }

        [RelayCommand]
        private void CancelInstall()
        {
            _cts?.Cancel();
        }
    }
}
