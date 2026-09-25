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

; Benchmark capture must ship with the tool: PresentMon 2.5.1 is pinned and
; hash-checked by the app at runtime, so refuse to COMPILE an installer from
; a publish folder that is missing it. Compile-time check (ISPP), not a
; runtime one - a finished Setup.exe must never depend on the build machine.
#if !FileExists(PublishDir + "\\PresentMon\\PresentMon.exe")
  #error Publish folder is missing PresentMon\\PresentMon.exe - Benchmark capture would be broken. Re-run Installer\\Build.ps1 (it publishes first).
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
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableWelcomePage=yes
; The app minimizes to tray and swallows WM_CLOSE, so Restart Manager
; cannot shut it down during PrepareToInstall — that aborts the upgrade
; with "Some applications could not be shut down" and rolls files back.
; We kill the process ourselves in PrepareToInstall instead.
CloseApplications=no
RestartApplications=no
; Always leave a log. A silent upgrade that fails is invisible otherwise:
; /SUPPRESSMSGBOXES defaults to Abort for every message it cannot show, so
; Setup rolls back and exits without telling anyone. The in-app updater also
; passes an explicit /LOG="…" path, but the app cannot pass one when the user
; runs Setup by hand (or when Setup is started from the updater's visible
; fallback), and this directive covers those runs too.
SetupLogging=yes
UninstallDisplayName={#MyAppName}
SetupIconFile=..\src\kaliteConfig\Assets\kaliteConfig.ico
UninstallDisplayIcon={app}\{#MyAppExe}

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; KaliteOS first-launch flag: 0 = needs Windhawk auto-provisioning, 1 = already done.
; Written at install time as 0; the app flips it to 1 after a successful Windhawk + mods import.
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: dword; ValueName: "IsInstalled"; ValueData: "0"; Flags: createvalueifdoesntexist uninsdeletekeyifempty
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: createvalueifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

[Run]
; shellexec is required: the app manifest is requireAdministrator, and plain
; CreateProcess cannot auto-elevate — it fails with error 740.
; postinstall without skipifsilent runs after SILENT installs too, so this
; single entry relaunches the app for the auto-updater (which exits the app
; so Setup can replace the files) as well as offering the Finish-page
; checkbox on interactive installs. The app requires admin anyway, so no
; runasoriginaluser demotion.
Filename: "{app}\{#MyAppExe}"; Parameters: "--tray"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall shellexec

[Code]
const
  BM_CLICK = $00F5;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpWelcome) or (CurPageID = wpReady) then
  begin
    PostMessage(WizardForm.NextButton.Handle, BM_CLICK, 0, 0);
  end;
end;

procedure KillRunningAppInstances;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM kaliteConfig.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM kaliteConfig-Consumer.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KillRunningAppInstances;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunningAppInstances;
  if CurStep = ssPostInstall then
  begin
    RegWriteStringValue(HKLM, 'SOFTWARE\KaliteOS', 'InstallPath', ExpandConstant('{app}'));
  end;
end;
