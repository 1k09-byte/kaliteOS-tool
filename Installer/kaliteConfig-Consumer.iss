; kaliteConfig Consumer installer (Inno Setup 6).
; Installs the CONSUMER flavor: same tool minus the power-plan settings.
; NOTE: folder + shortcut names deliberately differ from the full installer
; (kaliteConfig.iss) so the two flavors can never overwrite each other's
; exe, Start Menu entry, or desktop icon when both are present.
;
; Built by Installer/Build-Consumer.ps1, which publishes the app first and
; calls: ISCC /DPublishDir="<publish>" /DMyAppVersion="<version>" kaliteConfig-Consumer.iss
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\\publish\\consumer-win-x64"
#endif

#define MyAppName "kaliteConfig"
#define MyAppExe "kaliteConfig-Consumer.exe"

[Setup]
AppId={{3FBFD04D-76A3-415E-9B1C-096E04A1B7BC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} (Consumer)
DefaultDirName={autopf}\{#MyAppName} Consumer
DefaultGroupName={#MyAppName} Consumer
OutputDir=..\\dist
OutputBaseFilename={#MyAppName}-Consumer-Setup-{#MyAppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName} (Consumer)
SetupIconFile=..\\Assets\\kaliteConfig.ico
UninstallDisplayIcon={app}\\{#MyAppExe}

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Icons]
Name: "{group}\\{#MyAppName} Consumer"; Filename: "{app}\\{#MyAppExe}"
Name: "{autodesktop}\\{#MyAppName} Consumer"; Filename: "{app}\\{#MyAppExe}"; Tasks: desktopicon

[Run]
; shellexec is required: the app manifest is requireAdministrator, and plain
; CreateProcess cannot auto-elevate — it fails with error 740.
; The skipifsilent variant is intentionally NOT used for the silent (update)
; path: the auto-updater exits the app to let Setup replace the files, and
; the user expects the app to come back — so a silent run ALSO relaunches.
Filename: "{app}\\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall shellexec
Filename: "{app}\\{#MyAppExe}"; Description: ""; Flags: nowait skipifsilent runasoriginaluser shellexec
