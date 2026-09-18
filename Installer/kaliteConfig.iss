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
UninstallDisplayIcon={app}\{#MyAppExe}

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Registry]
; KaliteOS first-launch flag: 0 = needs Windhawk auto-provisioning, 1 = already done.
; Written at install time as 0; the app flips it to 1 after a successful Windhawk + mods import.
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: dword; ValueName: "IsInstalled"; ValueData: "0"; Flags: createvalueifdoesntexist uninsdeletekeyifempty
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: createvalueifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
; shellexec is required: the app manifest is requireAdministrator, and plain
; CreateProcess cannot auto-elevate — it fails with error 740.
; postinstall without skipifsilent runs after SILENT installs too, so this
; single entry relaunches the app for the auto-updater (which exits the app
; so Setup can replace the files) as well as offering the Finish-page
; checkbox on interactive installs. The app requires admin anyway, so no
; runasoriginaluser demotion.
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall shellexec

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Forcefully kill the running app because our custom minimize-to-tray logic
    // intercepts and blocks the Restart Manager's standard WM_CLOSE requests.
    Exec('taskkill.exe', '/F /IM kaliteConfig.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
