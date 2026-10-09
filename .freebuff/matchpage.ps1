param([int]$ProcId, [string]$StripFile)
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Small($src, $w) {
  $h = [Math]::Max(1, [int]($src.Height * $w / $src.Width))
  $b = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($src, 0, 0, $w, $h)
  $g.Dispose()
  return $b
}
$stripBmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $StripFile).Path)
$strip = Small $stripBmp 240
$sp = New-Object System.Drawing.Bitmap 240, $strip.Height
$pages = @("App Manager","Graphics Control","Process Affinity","Process Control","BIOS Control","Windows Tuning","Network Control","Benchmark Lab","Screen Capture")
$best = @()
foreach ($name in $pages) {
  & "$here\uia_select.ps1" -ProcId $ProcId -Name $name | Out-Null
  Start-Sleep -Seconds 2
  & "$here\cap3.ps1" $ProcId "m_($name -replace ' ','')" | Out-Null
  $f = Get-ChildItem "$here\win_m_*.png" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $capBmp = [System.Drawing.Bitmap]::FromFile($f.FullName)
  $cap = Small $capBmp 240
  $minScore = 1e9
  $minDy = -1
  for ($dy = 0; $dy -le ($cap.Height - $strip.Height); $dy++) {
    $score = 0.0; $n = 0
    for ($y = 0; $y -lt $strip.Height; $y += 2) {
      for ($x = 0; $x -lt 240; $x += 2) {
        $a = $strip.GetPixel($x, $y); $b = $cap.GetPixel($x, $y + $dy)
        $score += [Math]::Abs($a.R-$b.R) + [Math]::Abs($a.G-$b.G) + [Math]::Abs($a.B-$b.B)
        $n++
      }
    }
    $score = $score / $n
    if ($score -lt $minScore) { $minScore = $score; $minDy = $dy }
  }
  $best += "{0}: score={1:N1} at dy={2}" -f $name, $minScore, $minDy
  $capBmp.Dispose(); $cap.Dispose()
}
$stripBmp.Dispose(); $strip.Dispose()
$best
