param([string]$File)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
$colors = @{}
for ($y=0; $y -lt $bmp.Height; $y+=3) {
  for ($x=0; $x -lt $bmp.Width; $x+=3) {
    $c=$bmp.GetPixel($x,$y)
    $k = "#{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B
    if ($colors.ContainsKey($k)) { $colors[$k]++ } else { $colors[$k]=1 }
  }
}
$colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object { "{0} x{1}" -f $_.Key, $_.Value }
"---- light text rows (text-ish pixels per row) ----"
for ($y=0; $y -lt $bmp.Height; $y+=6) {
  $n=0
  for ($x=0; $x -lt $bmp.Width; $x+=3) {
    $c=$bmp.GetPixel($x,$y)
    if (($c.R + $c.G + $c.B) -gt 330) { $n++ }
  }
  if ($n -gt 5) { "y=$y : $n light px" }
}
$bmp.Dispose()
