# Does the app paint content that only appears after an external repaint?
#
# Samples a screen region, nudges the app window by one pixel (twice, so it ends
# up exactly where it started -- a real repaint with no visible change), samples
# again, and reports whether the pixels changed. A large change means the frame
# on screen was stale until something forced a recomposite.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools/repaint-probe.ps1 -Region 95,455,260,110
param(
    [string]$Region = "",
    [int]$SampleStep = 3,
    [string]$Points = ""      # optional "x,y;x,y;..." exact pixels to print before/after
)

Add-Type -AssemblyName System.Drawing

$sig = '[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);' +
       '[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int cx, int cy, uint flags);' +
       '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);' +
       '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);' +
       '[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }'
Add-Type -Namespace W -Name P -MemberDefinition $sig
$SWP_NOSIZE = 0x0001; $SWP_NOZORDER = 0x0004; $SWP_NOACTIVATE = 0x0010

$proc = Get-Process kaliteConfig -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Error "kaliteConfig has no window"; exit 1 }

$r = New-Object W.P+RECT
[void][W.P]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
if ($Region) { $x, $y, $w, $h = @($Region -split ',' | ForEach-Object { [int]$_ }) }
else { $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top }

function Probe-Points([string]$tag) {
    if (-not $Points) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    foreach ($pt in ($Points -split ';')) {
        if (-not $pt) { continue }
        $coords = @($pt -split ',' | ForEach-Object { [int]$_ })
        $c = $bmp.GetPixel($coords[0] - $x, $coords[1] - $y)
        [Console]::Out.WriteLine(("{0,-8} ({1},{2})  #{3:X2}{4:X2}{5:X2}" -f $tag, $coords[0], $coords[1], $c.R, $c.G, $c.B))
    }
    $bmp.Dispose()
}

function Sample([string]$tag) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $sum = 0.0; $n = 0; $sig = 0.0
    for ($py = 0; $py -lt $h; $py += $SampleStep) {
        for ($px = 0; $px -lt $w; $px += $SampleStep) {
            $c = $bmp.GetPixel($px, $py)
            $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            $sum += $l; $n++; $sig += $l * $l
        }
    }
    $bmp.Dispose()
    $mean = $sum / $n
    $sd = [Math]::Sqrt([Math]::Max(0, ($sig / $n) - $mean * $mean))
    $line = "{0,-8} mean={1,7:F2}  sd={2,7:F2}  samples={3}" -f $tag, $mean, $sd, $n
    [Console]::Out.WriteLine($line)
    $script:lastMean = [double]$mean
    $script:lastSd = [double]$sd
}

Write-Output "region ${x},${y} ${w}x${h}"
Sample "before"
Probe-Points "before"
[double]$meanBefore = $script:lastMean
[double]$sdBefore = $script:lastSd

# Nudge one pixel right/down and back: forces the compositor to produce a new frame.
$px = $r.Left; $py = $r.Top
[void][W.P]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $px + 1, $py + 1, 0, 0, $SWP_NOSIZE -bor $SWP_NOZORDER -bor $SWP_NOACTIVATE)
Start-Sleep -Milliseconds 250
[void][W.P]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $px, $py, 0, 0, $SWP_NOSIZE -bor $SWP_NOZORDER -bor $SWP_NOACTIVATE)
Start-Sleep -Milliseconds 500

Sample "nudged"
[double]$dMeanNudge = [Math]::Abs($script:lastMean - $meanBefore)
[double]$dSdNudge = [Math]::Abs($script:lastSd - $sdBefore)

# A same-position nudge can be coalesced away, so also force a repaint that cannot be
# missed: minimising and restoring rebuilds the whole window surface.
[void][W.P]::ShowWindow($proc.MainWindowHandle, 6)   # SW_MINIMIZE
Start-Sleep -Milliseconds 600
[void][W.P]::ShowWindow($proc.MainWindowHandle, 9)   # SW_RESTORE
[void][W.P]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Milliseconds 900

Sample "restored"
Probe-Points "after"
[double]$dMean = [Math]::Abs($script:lastMean - $meanBefore)
[double]$dSd = [Math]::Abs($script:lastSd - $sdBefore)
Write-Output ("delta nudge   mean={0,7:F2}  sd={1,7:F2}" -f $dMeanNudge, $dSdNudge)
Write-Output ("delta restore mean={0,7:F2}  sd={1,7:F2}" -f $dMean, $dSd)
if ($dMean -gt 1.0 -or $dSd -gt 2.0) {
    Write-Output "VERDICT: the frame changed after an external repaint -- the on-screen frame was STALE."
} else {
    Write-Output "VERDICT: pixels stable across a repaint -- what you see is what the app laid out."
}
