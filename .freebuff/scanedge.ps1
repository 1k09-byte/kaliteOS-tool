param([string]$File = "$PSScriptRoot\paste2.png", [int]$X, [int]$YStart = 0, [int]$YEnd = 233, [int]$Step = 8)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
"size: $($bmp.Width)x$($bmp.Height)"
if ($PSBoundParameters.ContainsKey('X')) {
  for ($y = $YStart; $y -le $YEnd; $y += $Step) {
    $c = $bmp.GetPixel($X, $y)
    '{0},{1} -> #{2:X2}{3:X2}{4:X2}' -f $X, $y, $c.R, $c.G, $c.B
  }
} else {
  # horizontal scan at several y values across x=0..200
  foreach ($y in @(10, 40, 80, 120, 160, 200, 230)) {
    $line = @()
    for ($x = 0; $x -le 200; $x += 4) {
      $c = $bmp.GetPixel($x, $y)
      $line += ('{0}:#{1:X2}{2:X2}{3:X2}' -f $x, $c.R, $c.G, $c.B)
    }
    "y=$y  " + ($line -join ' ')
  }
}
$bmp.Dispose()
