param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Platform = "x64",
    [bool]$Clean = $true
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectDir "publish\win-x64"
$issFile = Join-Path $PSScriptRoot "kaliteConfig.iss"

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

$csprojPath = Join-Path $projectDir "src\kaliteConfig\kaliteConfig.csproj"
$version = & dotnet msbuild $csprojPath -getProperty:Version
$version = ($version | Select-Object -First 1)
if (-not $version) { $version = "0.1.0" }
$version = ($version -split '\.')[0..3] -join '.'
Write-Host "Full build v$version ($Configuration/$Runtime)..."

Write-Host "--> dotnet publish (full flavor)..."
& dotnet publish $csprojPath `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Platform=$Platform `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "kaliteConfig.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output missing: $exe" }

$isccCmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($isccCmd) { $isccCmd.Source } else { $null }
foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
    if (-not $iscc -and (Test-Path -LiteralPath $candidate)) { $iscc = $candidate; break }
}
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6, then re-run. Publish output is ready at: $publishDir"
}

Write-Host "--> ISCC $issFile ..."
$stagingDir = Join-Path $env:TEMP "kaliteConfig-setup-staging"
if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
$isccDefines = @("/DPublishDir=$publishDir", "/DMyAppVersion=$version", "/O$stagingDir")
& $iscc @isccDefines $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$staged = Join-Path $stagingDir "kaliteConfig-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $staged)) { throw "Installer missing after ISCC: $staged" }
$distDir = Join-Path $projectDir "dist"
if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
$setup = Join-Path $distDir "kaliteConfig-Setup-$version.exe"
Move-Item -LiteralPath $staged -Destination $setup -Force
if (-not (Test-Path -LiteralPath $setup)) { throw "Installer missing after ISCC: $setup" }
Write-Host "OK: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
