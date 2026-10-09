param([string]$File = "$PSScriptRoot\win_gfx_overclock3.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
$glitch = @(
  ,(0x3F,0x20,0x43),(0x40,0x1F,0x43),(0x40,0x20,0x42),(0x3F,0x20,0x41),
  ,(0x3B,0x23,0x3B),(0x20,0x14,0x20),(0x1F,0x15,0x1F),(0x1F,0x14,0x1F),
  ,(0x1F,0x15,0x20),(0x34,0x27,0x34),(0x54,0x4C,0x55),(0x58,0x49,0x58),
  ,(0x4D,0x4E,0x58),(0x4E,0x4E,0x57),(0x13,0x20,0x2C)
)
function MatchesGlitch([System.Drawing.Color]$c) {
  foreach ($g in $glitch) {
    if ($c.R -eq $g[0] -and $c.G -eq $g[1] -and $c.B -eq $g[2]) { return $true }
  }
  return $false
}
$total = 0
$byY = @{}
for ($y = 0; $y -lt $bmp.Height; $y += $Step) {
  for ($x = 0; $x -lt $bmp.Width; $x += $Step) {
    $c = $bmp.GetPixel($x, $y)
    if (MatchesGlitch $c) {
      $total = $total + 1
      if ($byY.ContainsKey($y)) { $byY[$y] = $byY[$y] + 1 } else { $byY[$y] = 1 }
    }
  }
}
$bmp.Dispose()
Write-Output "exact-glitch pixels in capture: $total"
if ($total -eq 0) {
  Write-Output "NO glitch-colored pixels found - the purple band is NOT present in this capture"
} else {
  Write-Output "glitch pixels by y (top 20):"
  $byY.GetEnumerator() | Sort-Object Name | Select-Object -First 20 | ForEach-Object {
    Write-Output "  y=$($_.Key) : $($_.Value) px"
  }
}
