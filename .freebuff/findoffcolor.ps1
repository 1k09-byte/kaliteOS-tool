param([string]$File = "$PSScriptRoot\paste2.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
function IsAppColor([System.Drawing.Color]$c) {
  # chrome #10150F
  if ([Math]::Abs($c.R-16) -lt 8 -and [Math]::Abs($c.G-21) -lt 8 -and [Math]::Abs($c.B-15) -lt 8) { return $true }
  # card #111511
  if ([Math]::Abs($c.R-17) -lt 8 -and [Math]::Abs($c.G-21) -lt 8 -and [Math]::Abs($c.B-17) -lt 8) { return $true }
  # page bg #171A17/#181C18
  if ([Math]::Abs($c.R-23) -lt 8 -and [Math]::Abs($c.G-26) -lt 8 -and [Math]::Abs($c.B-23) -lt 8) { return $true }
  # button fill #2A322A
  if ([Math]::Abs($c.R-42) -lt 10 -and [Math]::Abs($c.G-50) -lt 10 -and [Math]::Abs($c.B-42) -lt 10) { return $true }
  # accent navy #1B3A6B
  if ([Math]::Abs($c.R-27) -lt 14 -and [Math]::Abs($c.G-58) -lt 14 -and [Math]::Abs($c.B-107) -lt 14) { return $true }
  # light text #B5BDAE / white
  if ($c.R -gt 150 -and $c.G -gt 150 -and $c.B -gt 150) { return $true }
  # near-black backdrop
  if ($c.R -lt 20 -and $c.G -lt 22 -and $c.B -lt 20) { return $true }
  return $false
}
$off = @()
for ($y = 0; $y -lt $bmp.Height; $y += $Step) {
  for ($x = 0; $x -lt $bmp.Width; $x += $Step) {
    $c = $bmp.GetPixel($x, $y)
    if (-not (IsAppColor $c)) {
      $off += "x=$x y=$y -> #$($c.R.ToString('X2'))$($c.G.ToString('X2'))$($c.B.ToString('X2'))"
    }
  }
}
$bmp.Dispose()
if ($off.Count -eq 0) {
  Write-Output "no off-palette pixels found"
} else {
  Write-Output "off-palette pixels: $($off.Count)"
  $off | Select-Object -First 40
}
