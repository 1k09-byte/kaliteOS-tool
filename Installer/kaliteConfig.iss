; kaliteConfig full installer (Inno Setup 6).
; Installs the FULL flavor: everything included (power-plan settings,
; uninstaller, startup manager, thread tuner, Windhawk provisioning, …).
;
; Built by Installer/Build.ps1, which publishes the app first and calls:
; ISCC /DPublishDir="<publish>" /DMyAppVersion="<version>" kaliteConfig.iss
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\\publish\\win-x64"
#endif

#define MyAppName "kaliteConfig"
#define MyAppExe "kaliteConfig.exe"

[Setup]
AppId={{422E40DD-B1A5-4157-907F-690182582441}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
SetupIconFile=..\\Assets\\kaliteConfig.ico
UninstallDisplayIcon={app}\\{#MyAppExe}

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Icons]
Name: "{group}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExe}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExe}"; Tasks: desktopicon

[Run]
; shellexec is required: the app manifest is requireAdministrator, and plain
; CreateProcess cannot auto-elevate — it fails with error 740.
Filename: "{app}\\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent shellexec
