param([string]$File)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
$bands = @{}
for ($y=0; $y -lt $bmp.Height; $y+=2) {
  $minX=99999; $maxX=0; $n=0
  $sr=0;$sg=0;$sb=0
  for ($x=0; $x -lt $bmp.Width; $x+=2) {
    $c=$bmp.GetPixel($x,$y)
    if (($c.R+$c.G+$c.B) -gt 330) {
      $n++
      if($x -lt $minX){$minX=$x}; if($x -gt $maxX){$maxX=$x}
      $sr+=$c.R;$sg+=$c.G;$sb+=$c.B
    }
  }
  if ($n -gt 4) {
    $key = [int]([Math]::Floor($y/12)*12)
    if (-not $bands.ContainsKey($key)) { $bands[$key] = @($minX,$maxX,0,0,0,0) }
    $b = $bands[$key]
    if ($minX -lt $b[0]) { $b[0]=$minX }
    if ($maxX -gt $b[1]) { $b[1]=$maxX }
    $b[2]+=$n; $b[3]+=$sr; $b[4]+=$sg; $b[5]+=$sb
    $bands[$key]=$b
  }
}
$bands.Keys | Sort-Object | ForEach-Object {
  $b=$bands[$_]
  $cnt=$b[2]
  $avg = ""
  if ($cnt -gt 0) { $avg = "avgColor=#{0:X2}{1:X2}{2:X2}" -f [int]($b[3]/$cnt),[int]($b[4]/$cnt),[int]($b[5]/$cnt) }
  "band y={0}: x={1}..{2} px={3} {4}" -f $_, $b[0], $b[1], $cnt, $avg
}
$bmp.Dispose()
