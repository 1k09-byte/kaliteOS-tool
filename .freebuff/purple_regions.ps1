param([string]$File = "$PSScriptRoot\paste2.png", [int]$Step = 2)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
$pixels = @()
for ($y = 0; $y -lt $bmp.Height; $y += $Step) {
    for ($x = 0; $x -lt $bmp.Width; $x += $Step) {
        $c = $bmp.GetPixel($x, $y)
        $r = $c.R; $g = $c.G; $b = $c.B
        if ($r -lt 25 -and $g -lt 28 -and $b -lt 25) { continue }
        if ($r -gt 140 -and $g -gt 140 -and $b -gt 140) { continue }
        if ([Math]::Abs($r-16) -le 8 -and [Math]::Abs($g-21) -le 8 -and [Math]::Abs($b-15) -le 8) { continue }
        if ([Math]::Abs($r-17) -le 8 -and [Math]::Abs($g-21) -le 8 -and [Math]::Abs($b-17) -le 8) { continue }
        if ([Math]::Abs($r-23) -le 8 -and [Math]::Abs($g-26) -le 8 -and [Math]::Abs($b-23) -le 8) { continue }
        if ([Math]::Abs($r-42) -le 10 -and [Math]::Abs($g-50) -le 10 -and [Math]::Abs($b-42) -le 10) { continue }
        if ([Math]::Abs($r-27) -le 16 -and [Math]::Abs($g-58) -le 16 -and [Math]::Abs($b-107) -le 16) { continue }
        $isPurple = $false
        if ($b -gt $r -and $b -gt $g + 8 -and $b -gt 35) { $isPurple = $true }
        if ($r -gt $g + 8 -and $b -gt $g + 8 -and $b -ge $r -and $r -lt 90 -and $b -lt 90 -and $r -gt 25) { $isPurple = $true }
        if ($isPurple) {
            $nb = [int]($y / 8)
            $pixels += [PsCustomObject]@{ x=$x; y=$y; nb=$nb; hex="$($r.ToString('X2'))$($g.ToString('X2'))$($b.ToString('X2'))" }
        }
    }
}
$bmp.Dispose()
Write-Output "total purple pixels: $($pixels.Count)"
$pixels | Group-Object nb | Sort-Object Name | ForEach-Object {
    $xs = $_.Group | Sort-Object x
    $mn = $xs[0].x
    $mx = $xs[-1].x
    $sample = $xs[0].hex
    Write-Output "y=$($_.Name*8)-$($_.Name*8+7): x=$mn..$mx  ($($_.Count) px) hex=$sample"
}
