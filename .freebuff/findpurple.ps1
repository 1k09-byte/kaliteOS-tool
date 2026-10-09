param([string]$File = "$PSScriptRoot\paste2.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
# purple signature: B > R > G (blue-purple) OR R>G and B>G with low overall (dark purple)
# We want pixels that are purplish AND not near-black and not app accent navy
$pulse = @()
for ($y = 0; $y -lt $bmp.Height; $y += $Step) {
  for ($x = 0; $x -lt $bmp.Width; $x += $Step) {
    $c = $bmp.GetPixel($x, $y)
    $r=$c.R; $g=$c.G; $b=$c.B
    # skip near-black and white/text
    if ($r -lt 25 -and $g -lt 28 -and $b -lt 25) { continue }
    if ($r -gt 140 -and $g -gt 140 -and $b -gt 140) { continue }
    # skip pure chrome #10150F
    if ([Math]::Abs($r-16) -lt 8 -and [Math]::Abs($g-21) -lt 8 -and [Math]::Abs($b-15) -lt 8) { continue }
    # skip card/page dark greens #111511/#171A17/#181C18
    if ([Math]::Abs($r-17) -lt 8 -and [Math]::Abs($g-21) -lt 8 -and [Math]::Abs($b-17) -lt 8) { continue }
    if ([Math]::Abs($r-23) -lt 8 -and [Math]::Abs($g-26) -lt 8 -and [Math]::Abs($b-23) -lt 8) { continue }
    # skip button #2A322A
    if ([Math]::Abs($r-42) -lt 10 -and [Math]::Abs($g-50) -lt 10 -and [Math]::Abs($b-42) -lt 10) { continue }
    # skip accent navy #1B3A6B (legitimate)
    if ([Math]::Abs($r-27) -lt 16 -and [Math]::Abs($g-58) -lt 16 -and [Math]::Abs($b-107) -lt 16) { continue }
    # PURPLE/Glitch signature: blue-purple hue.
    # purple-ish: b > r (blue-dominant) OR (r and b both notably > g) with b>=r (purple not navy)
    $isPurple = $false
    if ($b -gt $r -and $b -gt $g + 8 -and $b -gt 35) { $isPurple = $true }   # blue/purple dominant
    if ($r -gt $g + 8 -and $b -gt $g + 8 -and $b -ge $r -and $r -lt 90 -and $b -lt 90 -and $r -gt 25) { $isPurple = $true } # dark purple (r~=b>g)
    if ($isPurple) {
      $pulse += "x=$x y=$y -> #$($r.ToString('X2'))$($g.ToString('X2'))$($b.ToString('X2'))"
    }
  }
}
$bmp.Dispose()
if ($pulse.Count -eq 0) {
  Write-Output "no purple glitch pixels found"
} else {
  Write-Output "purple-ish pixels: $($pulse.Count)"
  $pulse | Select-Object -First 60
}
