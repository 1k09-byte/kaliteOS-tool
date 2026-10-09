param([string]$File)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
"size: {0}x{1}" -f $bmp.Width, $bmp.Height
# button fill #2A322A regions: per-row runs, report bboxes of horizontal runs > 20px
$runs = @{}
for ($y=0; $y -lt $bmp.Height; $y+=2) {
  $start = -1
  for ($x=0; $x -le $bmp.Width; $x+=2) {
    $isBtn = $false
    if ($x -lt $bmp.Width) {
      $c=$bmp.GetPixel($x,$y)
      $isBtn = ([Math]::Abs($c.R-42) -lt 10 -and [Math]::Abs($c.G-50) -lt 10 -and [Math]::Abs($c.B-42) -lt 10)
    }
    if ($isBtn -and $start -lt 0) { $start = $x }
    if ((-not $isBtn) -and $start -ge 0) {
      $len = $x - $start
      if ($len -gt 24) { "btn-run y=$y x=$start..$x (len $len)" }
      $start = -1
    }
  }
}
$bmp.Dispose()
