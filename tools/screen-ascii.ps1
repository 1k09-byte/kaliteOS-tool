# Renders the live kaliteConfig window as coarse ASCII art.
# Why: a screenshot cannot be read back by a terminal agent, but a downsampled
# luminance map can -- blank regions read as uniform characters, painted
# thumbnails/previews read as textured ones. That is how we tell "laid out but
# never painted" apart from "collapsed / zero-size" without human eyes.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/screen-ascii.ps1 [-Cols 120] [-Rows 46]
#   powershell -ExecutionPolicy Bypass -File tools/screen-ascii.ps1 -Region 93,453,300,160
param(
    [int]$Cols = 120,
    [int]$Rows = 46,
    [string]$Region = "",      # x,y,w,h in screen coordinates; default = whole window
    [switch]$Stats,            # also print per-row statistics for the region
    [string]$Scanline = ""     # x,y0,y1: print raw pixel colours down one column (exact paints)
)

Add-Type -AssemblyName System.Drawing

$sig = '[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);' +
       '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);' +
       '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);' +
       '[DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);' +
       '[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }'
Add-Type -Namespace W -Name R -MemberDefinition $sig

$proc = Get-Process kaliteConfig -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Error "kaliteConfig has no window"; exit 1 }

# CopyFromScreen sees the composited desktop, so anything floating above the app
# would be sampled instead of the app. Raise it first.
if ([W.R]::IsIconic($proc.MainWindowHandle)) { [void][W.R]::ShowWindow($proc.MainWindowHandle, 9) }
[void][W.R]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Milliseconds 400

$regionParts = @($Region -split ',' | Where-Object { $_ -ne "" }) | ForEach-Object { [int]$_ }
if ($regionParts.Count -eq 4) {
    $x, $y, $w, $h = $regionParts
} else {
    $r = New-Object W.R+RECT
    [void][W.R]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
}

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()

$scan = @($Scanline -split ',' | Where-Object { $_ -ne "" }) | ForEach-Object { [int]$_ }
if ($scan.Count -eq 3) {
    $sx, $sy0, $sy1 = $scan
    $prev = ""
    for ($sy = $sy0; $sy -le $sy1; $sy++) {
        $c = $bmp.GetPixel($sx - $x, $sy - $y)
        $cur = "{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
        if ($cur -ne $prev) {
            Write-Output ("y={0,5}  #{1}  ({2},{3},{4})" -f $sy, $cur, $c.R, $c.G, $c.B)
            $prev = $cur
        }
    }
    $bmp.Dispose()
    exit 0
}

Write-Output "window=${w}x${h} at ${x},${y}   sampling ${Cols}x${Rows}"

$ramp = " .:-=+*#%@"
$cellW = [Math]::Max(1, [int]($w / $Cols))
$cellH = [Math]::Max(1, [int]($h / $Rows))

# Two passes: measure every cell, then stretch contrast across the *window's own*
# range. The app is a dark theme, so raw luminance squashes into the first two
# ramp characters and nothing is legible.
$means = New-Object 'double[,]' $Rows, $Cols
$contrast = New-Object 'double[,]' $Rows, $Cols
for ($cy = 0; $cy -lt $Rows; $cy++) {
    for ($cx = 0; $cx -lt $Cols; $cx++) {
        $sum = 0.0; $n = 0; $min = 255.0; $max = 0.0
        for ($py = $cy * $cellH; $py -lt [Math]::Min(($cy + 1) * $cellH, $h); $py += 2) {
            for ($px = $cx * $cellW; $px -lt [Math]::Min(($cx + 1) * $cellW, $w); $px += 2) {
                $c = $bmp.GetPixel($px, $py)
                $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                $sum += $l; $n++
                if ($l -lt $min) { $min = $l }
                if ($l -gt $max) { $max = $l }
            }
        }
        $means[$cy, $cx] = if ($n -gt 0) { $sum / $n } else { 0 }
        $contrast[$cy, $cx] = $max - $min
    }
}

$flat = @()
for ($cy = 0; $cy -lt $Rows; $cy++) { for ($cx = 0; $cx -lt $Cols; $cx++) { $flat += $means[$cy, $cx] } }
$sorted = $flat | Sort-Object
$lo = $sorted[[int]($sorted.Count * 0.05)]
$hi = $sorted[[int]($sorted.Count * 0.98)]
if ($hi - $lo -lt 24) { $hi = $lo + 24 }

for ($cy = 0; $cy -lt $Rows; $cy++) {
    $line = ""
    for ($cx = 0; $cx -lt $Cols; $cx++) {
        $t = ($means[$cy, $cx] - $lo) / ($hi - $lo)
        $t = [Math]::Max(0, [Math]::Min(1, $t))
        $idx = [Math]::Min($ramp.Length - 1, [int]($t * ($ramp.Length - 1)))
        $ch = [string]$ramp[$idx]
        # Local contrast = real content (image pixels, text, glyphs). Flat fills stay
        # as their brightness character, so a never-painted card is unmistakable.
        if ($contrast[$cy, $cx] -gt 90) { $ch = "@" }
        elseif ($contrast[$cy, $cx] -gt 45) { $ch = "#" }
        elseif ($contrast[$cy, $cx] -gt 20) { $ch = "+" }
    $line += $ch
    }
    Write-Output $line
}

if ($Stats) {
    Write-Output ""
    Write-Output "row stats (rows with any high-contrast cell):"
    for ($cy = 0; $cy -lt $Rows; $cy++) {
        $contrast = 0
        for ($cx = 0; $cx -lt $Cols; $cx++) {
            $min = 255.0; $max = 0.0
            for ($py = $cy * $cellH; $py -lt [Math]::Min(($cy + 1) * $cellH, $h); $py += 2) {
                for ($px = $cx * $cellW; $px -lt [Math]::Min(($cx + 1) * $cellW, $w); $px += 2) {
                    $c = $bmp.GetPixel($px, $py)
                    $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                    if ($l -lt $min) { $min = $l }
                    if ($l -gt $max) { $max = $l }
                }
            }
            if (($max - $min) -gt 60) { $contrast++ }
        }
        if ($contrast -gt 0) { Write-Output ("  y={0,4}..{1,4}  textured cells: {2}" -f ($cy * $cellH), (($cy + 1) * $cellH), $contrast) }
    }
}

$bmp.Dispose()
