param([string]$File = "$PSScriptRoot\win_gfx_overclock3.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
function Sig([System.Drawing.Color]$c) {
  $r=$c.R; $g=$c.G; $b=$c.B
  # the glitch signature: dark plum/purple, R and B > G, not near-black, not chrome #10150F
  if ($r -lt 25 -and $g -lt 28 -and $b -lt 25) { return "near-black" }
  if ([Math]::Abs($r-16) -le 8 -and [Math]::Abs($g-21) -le 8 -and [Math]::Abs($b-15) -le 8) { return "chrome-10150F" }
  if ($b -gt $r -and $b -gt $g + 8 -and $b -gt 35) { return "purple-blue" }
  if ($r -gt $g + 8 -and $b -gt $g + 8 -and $b -ge $r -and $r -lt 90 -and $b -lt 90 -and $r -gt 25) { return "purple-plum" }
  return "other"
}
Write-Output "width=$($bmp.Width) height=$($bmp.Height)"
for ($y = 0; $y -lt $bmp.Height; $y += 4) {
  $counts = @{}
  for ($x = 0; $x -le $bmp.Width; $x += $Step) {
    $s = Sig($bmp.GetPixel($x, $y))
    if ($counts.ContainsKey($s)) { $counts[$s] = $counts[$s] + 1 } else { $counts[$s] = 1 }
  }
  $tot = 0; foreach ($v in $counts.Values) { $tot = $tot + $v }
  $plum = if ($counts.ContainsKey('purple-plum')) { $counts['purple-plum'] } else { 0 }
  $purp = if ($counts.ContainsKey('purple-blue')) { $counts['purple-blue'] } else { 0 }
  $chr = if ($counts.ContainsKey('chrome-10150F')) { $counts['chrome-10150F'] } else { 0 }
  $nb = if ($counts.ContainsKey('near-black')) { $counts['near-black'] } else { 0 }
  if ($plum + $purp -gt 3) {
    Write-Output "y=$y  plum=$plum purp=$purp chrome=$chr nearblack=$nb  tot=$tot"
  }
}
$bmp.Dispose()
