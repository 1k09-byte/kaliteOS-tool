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
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
DisableWelcomePage=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallDisplayName={#MyAppName}
SetupIconFile=..\src\kaliteConfig\Assets\kaliteConfig.ico
UninstallDisplayIcon={app}\{#MyAppExe}

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: dword; ValueName: "IsInstalled"; ValueData: "0"; Flags: createvalueifdoesntexist uninsdeletekeyifempty
Root: HKLM; Subkey: "SOFTWARE\KaliteOS"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: createvalueifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--tray"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall shellexec

[Code]
const
  BM_CLICK = $00F5;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

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
