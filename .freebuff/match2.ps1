param([int]$ProcId, [string]$StripFile)
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Small($src, $w) {
  $h = [Math]::Max(1, [int]($src.Height * $w / $src.Width))
  $b = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.DrawImage($src, 0, 0, $w, $h)
  $g.Dispose()
  return , $b
}
$stripBmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $StripFile).Path)
$pages = @("App Manager","Graphics Control","Process Affinity","Process Control","BIOS Control","Windows Tuning","Network Control","Benchmark Lab","Screen Capture")
foreach ($name in $pages) {
  & "$here\uia_select.ps1" -ProcId $ProcId -Name $name | Out-Null
  Start-Sleep -Seconds 3
  $tag = "p_" + ($name -replace ' ', '_')
  & "$here\cap3.ps1" $ProcId $tag | Out-Null
  $capBmp = [System.Drawing.Bitmap]::FromFile("$here\win_$tag.png")
  # crop the content area (skip 65px rail) and compare at equal scale
  $bestScore = 1e9; $bestDy = -1; $bestDx = -1
  foreach ($dx in @(65, 0)) {
    if ($capBmp.Width - $dx -lt $stripBmp.Width) { continue }
    for ($dy = 0; $dy -le ($capBmp.Height - $stripBmp.Height); $dy += 3) {
      $score = 0.0; $n = 0
      for ($y = 0; $y -lt $stripBmp.Height; $y += 4) {
        for ($x = 0; $x -lt $stripBmp.Width; $x += 6) {
          $a = $stripBmp.GetPixel($x, $y); $b = $capBmp.GetPixel($x + $dx, $y + $dy)
          $score += [Math]::Abs($a.R-$b.R) + [Math]::Abs($a.G-$b.G) + [Math]::Abs($a.B-$b.B)
          $n++
        }
      }
      $score = $score / $n
      if ($score -lt $bestScore) { $bestScore = $score; $bestDy = $dy; $bestDx = $dx }
    }
  }
  "{0}: score={1:N1} at dx={2} dy={3}" -f $name, $bestScore, $bestDx, $bestDy
  $capBmp.Dispose()
}
$stripBmp.Dispose()
