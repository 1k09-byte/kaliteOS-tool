param([string]$File)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
function BBox($match, $name) {
  $minX=99999;$minY=99999;$maxX=0;$maxY=0;$n=0
  for ($y=0; $y -lt $bmp.Height; $y+=3) {
    for ($x=0; $x -lt $bmp.Width; $x+=3) {
      $c=$bmp.GetPixel($x,$y)
      if (& $match $c) {
        $n++
        if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}
        if($y -lt $minY){$minY=$y}; if($y -gt $maxY){$maxY=$y}
      }
    }
  }
  "{0}: n={1} bbox=({2},{3})-({4},{5})" -f $name,$n,$minX,$minY,$maxX,$maxY
}
BBox { param($c) [Math]::Abs($c.R-16) -lt 3 -and [Math]::Abs($c.G-21) -lt 3 -and [Math]::Abs($c.B-15) -lt 3 } "chrome #10150F"
BBox { param($c) [Math]::Abs($c.R-27) -lt 8 -and [Math]::Abs($c.G-58) -lt 8 -and [Math]::Abs($c.B-107) -lt 10 } "navy #1B3A6B"
BBox { param($c) $c.R -gt 200 -and $c.G -gt 220 -and $c.B -lt 130 } "lime #C6F36B"
BBox { param($c) $c.R -gt 220 -and $c.G -gt 140 -and $c.G -lt 200 -and $c.B -lt 90 } "caution #F5A524"
BBox { param($c) $c.R -gt 100 -and $c.R -lt 150 -and $c.G -gt 140 -and $c.G -lt 190 -and $c.B -gt 200 } "accent text #7FA6DC"
$bmp.Dispose()
