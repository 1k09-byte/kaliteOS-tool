<#
.SYNOPSIS
  One-click consumer build: publishes the CONSUMER flavor
  (no power-plan settings) and compiles
  the Inno Setup installer into dist\kaliteConfig-Consumer-Setup-<ver>.exe
.EXAMPLE
  .\Build-Consumer.ps1
  .\Build-Consumer.ps1 -Configuration Release -Runtime win-x64
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectDir "publish\consumer-win-x64"
$issFile    = Join-Path $PSScriptRoot "kaliteConfig-Consumer.iss"

# Version straight from the csproj so installer + app never drift.
[xml]$csproj = Get-Content (Join-Path $projectDir "kaliteConfig.csproj")
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "0.1.0" }
$version = ($version -split '\.')[0..2] -join '.'
Write-Host "Consumer build v$version ($Configuration/$Runtime)..."

Write-Host "--> dotnet publish (ConsumerBuild)..."
& dotnet publish (Join-Path $projectDir "kaliteConfig.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Platform=$Platform -p:ConsumerBuild=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "kaliteConfig-Consumer.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output missing: $exe" }

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
& $iscc "/DPublishDir=$publishDir" "/DMyAppVersion=$version" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$setup = Join-Path $projectDir "dist\kaliteConfig-Consumer-Setup-$version.exe"
Write-Host "OK: $setup"
