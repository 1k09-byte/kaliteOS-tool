param([string]$File)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
"{0}x{1}" -f $bmp.Width, $bmp.Height
# find red-ish text pixels (error text is #E5484D family) and their bbox
$minX=99999;$minY=99999;$maxX=0;$maxY=0;$count=0
for ($y=0; $y -lt $bmp.Height; $y+=2) {
  for ($x=0; $x -lt $bmp.Width; $x+=2) {
    $c=$bmp.GetPixel($x,$y)
    if ($c.R -gt 180 -and $c.G -gt 50 -and $c.G -lt 120 -and $c.B -gt 50 -and $c.B -lt 120) {
      $count++
      if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}
      if($y -lt $minY){$minY=$y}; if($y -gt $maxY){$maxY=$y}
    }
  }
}
"red pixels: $count bbox: ($minX,$minY)-($maxX,$maxY)"
# background sample
$c=$bmp.GetPixel([int]($bmp.Width/2), [int]($bmp.Height/2)); "center: #{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B
$bmp.Dispose()
