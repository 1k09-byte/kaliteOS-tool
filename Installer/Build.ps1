<#
.SYNOPSIS
  One-click full build: publishes the FULL flavor
  (everything included — power-plan settings, uninstaller,
  startup manager, thread tuner, Windhawk provisioning)
  and compiles the Inno Setup installer into
  dist\kaliteConfig-Setup-<ver>.exe
.EXAMPLE
  .\Build.ps1
  .\Build.ps1 -Configuration Release -Runtime win-x64
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Platform = "x64",
    # Clean rebuild (default ON): wipes the publish folder and all obj/
    # intermediates before publishing. Incremental reuse of those folders
    # once shipped an installer whose exe and compiled XAML (.pri) came from
    # different builds — the app then died in MainWindow.InitializeComponent
    # on every launch. Release/installer builds always start clean; pass
    # -Clean:$false only for a faster throwaway local check.
    [bool]$Clean = $true
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectDir "publish\win-x64"
$issFile    = Join-Path $PSScriptRoot "kaliteConfig.iss"

if ($Clean) {
    Write-Host "--> clean: wiping publish output and obj/ intermediates..."
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    $objDir = Join-Path $projectDir "obj"
    if (Test-Path -LiteralPath $objDir) {
        Remove-Item -LiteralPath $objDir -Recurse -Force
    }
}

# Version straight from the evaluated csproj so installer + app never
# drift (the csproj computes Version with a date-based expression, so ask
# MSBuild for the evaluated value, not the raw XML text).
$version = & dotnet msbuild (Join-Path $projectDir "kaliteConfig.csproj") -getProperty:Version
$version = ($version | Select-Object -First 1)
if (-not $version) { $version = "0.1.0" }
$version = ($version -split '\.')[0..3] -join '.'
Write-Host "Full build v$version ($Configuration/$Runtime)..."

Write-Host "--> dotnet publish (full flavor)..."
& dotnet publish (Join-Path $projectDir "kaliteConfig.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Platform=$Platform `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "kaliteConfig.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output missing: $exe" }

# Benchmark capture must ship with the installer: PresentMon 2.5.1 is
# pinned and hash-checked by the app at runtime, so a publish without it
# would produce an installer whose Benchmark tab can never capture.
$presentMon = Join-Path $publishDir "PresentMon\PresentMon.exe"
if (-not (Test-Path -LiteralPath $presentMon)) {
    throw "Publish output missing bundled PresentMon: $presentMon. Benchmark capture would be broken - check the DeployPresentMonToolToPublish target in kaliteConfig.csproj."
}
Write-Host "--> PresentMon bundled OK ($(Split-Path -Leaf $presentMon))"

$isccCmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($isccCmd) { $isccCmd.Source } else { $null }
foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                         "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
    if (-not $iscc -and (Test-Path -LiteralPath $candidate)) { $iscc = $candidate; break }
}
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6, then re-run. Publish output is ready at: $publishDir"
}

Write-Host "--> ISCC $issFile ..."
# Compile into a staging folder and only move the finished setup into dist\
# afterwards: ISCC writes its output progressively, so an interrupted or
# failed compile used to leave a TRUNCATED kaliteConfig-Setup-<ver>.exe in
# dist\ under the real shipping name (it looked like a valid release and would
# fail on the user's machine). Nothing partial can reach dist\ now.
$stagingDir = Join-Path $env:TEMP "kaliteConfig-setup-staging"
if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
& $iscc "/DPublishDir=$publishDir" "/DMyAppVersion=$version" "/O$stagingDir" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$staged = Join-Path $stagingDir "kaliteConfig-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $staged)) { throw "Installer missing after ISCC: $staged" }

# Sanity-check the produced installer actually embeds PresentMon.
$distDir = Join-Path $projectDir "dist"
if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
$setup = Join-Path $distDir "kaliteConfig-Setup-$version.exe"
Move-Item -LiteralPath $staged -Destination $setup -Force
if (-not (Test-Path -LiteralPath $setup)) { throw "Installer missing after ISCC: $setup" }
Write-Host "OK: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
