$ErrorActionPreference = "Stop"

Write-Host "Downloading GH CLI..."
Invoke-WebRequest -Uri "https://github.com/cli/cli/releases/download/v2.55.0/gh_2.55.0_windows_amd64.zip" -OutFile "gh.zip"
if (Test-Path "gh_cli") { Remove-Item "gh_cli" -Recurse -Force }
Expand-Archive -Path "gh.zip" -DestinationPath "gh_cli"

$ghExe = (Get-ChildItem -Path "gh_cli" -Filter "gh.exe" -Recurse).FullName
Write-Host "GH CLI found at $ghExe"

Write-Host "Getting token..."
$cred = "url=https://github.com`n" | git credential fill
$tokenLine = $cred | Where-Object { $_ -match "^password=" }
if (-not $tokenLine) { throw "No token found!" }
$token = $tokenLine -replace "^password=", ""

$env:GH_TOKEN = $token

Write-Host "Creating release..."
& $ghExe release create "v0.3.0.21" "C:\Users\Administrator\source\repos\kaliteconfig\dist\kaliteConfig-Setup-0.3.0.21.exe" --title "v0.3.0.21" --notes-file "C:\Users\Administrator\source\repos\kaliteconfig\changelog.txt" --repo "1k09-byte/kaliteOS-tool"

Write-Host "Done!"
