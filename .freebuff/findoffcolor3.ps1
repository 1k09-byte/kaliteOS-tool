param([string]$File = "$PSScriptRoot\paste2.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
function IsAppColor([System.Drawing.Color]$c) {
  if ([Math]::Abs($c.R-16) -lt 8 -and [Math]::Abs($c.G-21) -lt 8 -and [Math]::Abs($c.B-15) -lt 8) { return $true }
  if ([Math]::Abs($c.R-17) -lt 8 -and [Math]::Abs($c.G-21) -lt 8 -and [Math]::Abs($c.B-17) -lt 8) { return $true }
  if ([Math]::Abs($c.R-23) -lt 8 -and [Math]::Abs($c.G-26) -lt 8 -and [Math]::Abs($c.B-23) -lt 8) { return $true }
  if ([Math]::Abs($c.R-42) -lt 10 -and [Math]::Abs($c.G-50) -lt 10 -and [Math]::Abs($c.B-42) -lt 10) { return $true }
  if ([Math]::Abs($c.R-27) -lt 14 -and [Math]::Abs($c.G-58) -lt 14 -and [Math]::Abs($c.B-107) -lt 14) { return $true }
  if ($c.R -gt 150 -and $c.G -gt 150 -and $c.B -gt 150) { return $true }
  if ($c.R -lt 20 -and $c.G -lt 22 -and $c.B -lt 20) { return $true }
  return $false
}
# red-dominant: R notably exceeds both G and B
function IsRed([System.Drawing.Color]$c) {
  if ($c.R -gt 90 -and $c.R -gt $c.G + 40 -and $c.R -gt $c.B + 40) { return $true }
  return $false
}
$off = @()
for ($y = 0; $y -lt $bmp.Height; $y += $Step) {
  for ($x = 0; $x -lt $bmp.Width; $x += $Step) {
    $c = $bmp.GetPixel($x, $y)
    if (IsRed $c) { continue }
    if (-not (IsAppColor $c)) {
      $off += "x=$x y=$y -> #$($c.R.ToString('X2'))$($c.G.ToString('X2'))$($c.B.ToString('X2'))"
    }
  }
}
$bmp.Dispose()
if ($off.Count -eq 0) {
  Write-Output "no genuine off-palette pixels found"
} else {
  Write-Output "genuine off-palette pixels: $($off.Count)"
  $off | Select-Object -First 80
}
