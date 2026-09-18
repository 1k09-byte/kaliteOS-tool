<#
.SYNOPSIS
  One-command release: bumps <Version>, commits, tags, and pushes.
  Pushing the tag triggers .github/workflows/release.yml, which builds the
  installer and publishes the GitHub Release (setup exe + generated notes)
  that the in-app updater picks up.
.EXAMPLE
  .\Release-NewVersion.ps1 -Version 0.3.0.2
.NOTES
  Version must be 4-part numeric and NEWER than the current csproj version.
  The CI workflow refuses tags that don't match the csproj, so this script
  is the only sane way to cut a release (no more "Release v{...}" commits).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $projectDir "kaliteConfig.csproj"

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must be 4-part numeric (e.g. 0.3.0.2), got: $Version"
}

[xml]$xml = Get-Content -LiteralPath $csproj
$current = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($current)) { throw "Could not read <Version> from kaliteConfig.csproj." }

function Compare-Version4([string]$a, [string]$b) {
    $pa = $a.Split('.') | ForEach-Object { [int]$_ }
    $pb = $b.Split('.') | ForEach-Object { [int]$_ }
    for ($i = 0; $i -lt 4; $i++) {
        if ($pa[$i] -ne $pb[$i]) { return $pa[$i] - $pb[$i] }
    }
    return 0
}

if ((Compare-Version4 $Version $current) -le 0) {
    throw "New version $Version must be newer than current csproj version $current."
}

$tag = "v$Version"
$existingTag = & git tag -l $tag
if ($existingTag) { throw "Tag $tag already exists — pick a newer version." }

$status = & git status --porcelain
if ($status) {
    Write-Warning "Working tree has uncommitted changes — they will NOT be part of this release (only the version bump commits)."
    Write-Warning ($status -join "`n")
}

# Bump the real <Version> element (anchored to line start so the comment
# mentioning <Version> elsewhere in the file can never match).
$text = Get-Content -LiteralPath $csproj -Raw
$newText = $text -replace '(?m)^(\s*)<Version>\d+\.\d+\.\d+\.\d+</Version>', "`$1<Version>$Version</Version>"
if ($newText -eq $text) { throw "Version element not found — csproj layout changed?" }
Set-Content -LiteralPath $csproj -Value $newText -NoNewline:$false

& git add $csproj
& git commit -m "Release v$Version"
if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
& git tag $tag
if ($LASTEXITCODE -ne 0) { throw "git tag failed." }

Write-Host "Pushing commit and tag $tag (CI builds + publishes the release)..."
& git push
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
& git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "git push of tag $tag failed." }

Write-Host ""
Write-Host "OK: $tag pushed. Watch the Actions tab — when the workflow finishes,"
Write-Host "the release (setup exe + notes) is live and installed apps will offer it on next launch."
