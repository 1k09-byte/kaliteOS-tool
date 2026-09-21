<#
.SYNOPSIS
  Reproduces the in-app silent upgrade the way the updater does it, and proves
  whether it took: starts the INSTALLED app, runs the setup exe with the exact
  argument set UpdateCheckService.BuildSilentInstallerArguments produces, then
  reports the resulting versions plus Setup's own log.

  This is the test that was missing when "Update now" silently did nothing:
  a /VERYSILENT Inno run with /SUPPRESSMSGBOXES defaults to Abort for every
  message it cannot show, so without /LOG a failed upgrade leaves no trace.

.PARAMETER SetupPath
  Setup exe to install (default: the newest dist\kaliteConfig-Setup-*.exe).

.PARAMETER Version
  Version the setup carries, used for the log name (default: parsed from name).

.PARAMETER TimeoutSeconds
  How long to wait for Setup before giving up.
#>
param(
    [string]$SetupPath,
    [string]$Version,
    [int]$TimeoutSeconds = 420,
    [switch]$SkipAppStart
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$appExe = "C:\Program Files\kaliteConfig\kaliteConfig.exe"
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{422E40DD-B1A5-4157-907F-690182582441}_is1"

function Show-Version([string]$exePath) {
    if (-not (Test-Path -LiteralPath $exePath)) { return "(missing)" }
    $item = Get-Item -LiteralPath $exePath
    return "$($item.VersionInfo.FileVersion)  [$($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))]"
}

if (-not $SetupPath) {
    $SetupPath = Get-ChildItem -LiteralPath (Join-Path $projectDir "dist") -Filter "kaliteConfig-Setup-*.exe" |
        Sort-Object { [version](($_.BaseName -split 'Setup-')[1]) } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not (Test-Path -LiteralPath $SetupPath)) { throw "Setup not found: $SetupPath" }
if (-not $Version) {
    $Version = ([System.IO.Path]::GetFileNameWithoutExtension($SetupPath) -split 'Setup-')[1]
}

Write-Host "=== kaliteConfig silent-upgrade probe ===" -ForegroundColor Cyan
Write-Host "setup      : $SetupPath ($([math]::Round((Get-Item -LiteralPath $SetupPath).Length / 1MB, 1)) MB)"
Write-Host "version    : $Version"
Write-Host "installed  : $(Show-Version $appExe)"
Write-Host "uninstall  : $((Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).DisplayVersion)"
Write-Host ""

$logPath = Join-Path $env:TEMP "kaliteConfig-setup-$Version.log"
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

if (-not $SkipAppStart) {
    Write-Host "--> launching the installed app so Setup must upgrade over a RUNNING exe (the tray app blocks WM_CLOSE)..."
    Start-Process -FilePath $appExe | Out-Null
    Start-Sleep -Seconds 12
    $running = @(Get-Process -Name kaliteConfig -ErrorAction SilentlyContinue)
    Write-Host "--> running instances: $($running.Count) (pid $(($running | Select-Object -ExpandProperty Id) -join ','))"
}

# The exact argument set the updater builds:
#   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /LOG="…" /DIR="…"
$arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS " +
             "/LOG=`"$logPath`" /DIR=`"C:\Program Files\kaliteConfig`""
Write-Host "--> setup $arguments"
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $SetupPath -ArgumentList $arguments -PassThru
if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Warning "Setup still running after $TimeoutSeconds s — killing it."
    $proc.Kill()
}
$sw.Stop()
Write-Host "--> setup exited with $($proc.ExitCode) after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
Write-Host ""

Write-Host "=== result ===" -ForegroundColor Cyan
Write-Host "installed  : $(Show-Version $appExe)"
Write-Host "uninstall  : $((Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).DisplayVersion)"
Write-Host "running    : $((@(Get-Process -Name kaliteConfig -ErrorAction SilentlyContinue) | ForEach-Object { $_.Id }) -join ',')"
$taken = (Test-Path -LiteralPath $appExe) -and
         ((Get-Item -LiteralPath $appExe).VersionInfo.FileVersion -eq $Version)
Write-Host "VERDICT    : $(if ($taken) { "UPGRADE TOOK" } else { "UPGRADE DID NOT TAKE" })" `
    -ForegroundColor $(if ($taken) { "Green" } else { "Red" })

if (Test-Path -LiteralPath $logPath) {
    Write-Host ""
    Write-Host "--- $logPath (tail) ---"
    Get-Content -LiteralPath $logPath -Tail 30
} else {
    Write-Warning "No Setup log at $logPath — Setup never got far enough to write one."
}
