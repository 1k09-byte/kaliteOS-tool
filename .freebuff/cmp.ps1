Add-Type -AssemblyName System.Drawing
$root = "E:\Users\Administrator\source\repos\kaliteconfig"
$uncial = New-Object System.Drawing.Text.PrivateFontCollection
$uncial.AddFontFile("$root\src\kaliteConfig\Assets\Fonts\UncialAntiqua-Regular.ttf")

function Map([System.Drawing.Bitmap]$b, [string]$tag) {
  $cols = [Math]::Min($b.Width, 190); $rows = [Math]::Min($b.Height, 26)
  Write-Host "--- $tag ($($b.Width)x$($b.Height))"
  for ($ry=0; $ry -lt $rows; $ry++) {
    $line=''
    for ($cx=0; $cx -lt $cols; $cx++) {
      $x=[int](($cx+0.5)*$b.Width/$cols); $y=[int](($ry+0.5)*$b.Height/$rows)
      $c=$b.GetPixel($x,$y); $l=($c.R+$c.G+$c.B)/3
      if ($l -lt 30) { $ch='.' } elseif ($l -lt 75) { $ch='o' } elseif ($l -lt 140) { $ch='-' } else { $ch='O' }
      $line+=$ch
    }
    Write-Host ('{0,2} |{1}|' -f $ry,$line)
  }
}
function Render([string]$text, [System.Drawing.Font]$f, [int]$w, [int]$h, [string]$tag) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Black)
  $g.DrawString($text, $f, [System.Drawing.Brushes]::White, 2, 2)
  $g.Dispose()
  Map $bmp $tag
}

# the app's caption line from the before capture
$app = New-Object System.Drawing.Bitmap "$root\.freebuff\win_before.png"
$crop = $app.Clone((New-Object System.Drawing.Rectangle(115, 136, 210, 26)), $app.PixelFormat)
Map $crop "APP caption line (x115-325,y136-162)"

$u12 = New-Object System.Drawing.Font($uncial.Families[0].Name, 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$s12 = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
Render "Show installed" $u12 210 26 "REF Uncial12 'Show installed'"
Render "Show installed" $s12 210 26 "REF Segoe12 'Show installed'"