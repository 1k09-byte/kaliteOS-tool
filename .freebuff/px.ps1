param([string]$File, [int]$X = 800, [int]$YStart = 0, [int]$YEnd = 70, [int]$Step = 4)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
for ($y = $YStart; $y -le $YEnd; $y += $Step) {
    $c = $bmp.GetPixel($X, $y)
    '{0},{1} -> #{2:X2}{3:X2}{4:X2}' -f $X, $y, $c.R, $c.G, $c.B
}
$bmp.Dispose()
