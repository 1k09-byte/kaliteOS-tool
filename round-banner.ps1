Add-Type -AssemblyName System.Drawing
$inPath = 'C:\Users\Administrator\source\repos\kaliteconfig\Docs\kaliteConfig_banner.png'
$outPath = 'C:\Users\Administrator\source\repos\kaliteconfig\Docs\kaliteConfig_banner_rounded.png'

$img = [System.Drawing.Image]::FromFile($inPath)
$bmp = New-Object System.Drawing.Bitmap($img.Width, $img.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

$graphicsPath = New-Object System.Drawing.Drawing2D.GraphicsPath

# Calculate radius as 12% of the smallest dimension
$radius = [math]::Round([math]::Min($img.Width, $img.Height) * 0.12)
if ($radius -lt 1) { $radius = 1 }
$d = $radius * 2

$rect = New-Object System.Drawing.Rectangle(0, 0, $img.Width, $img.Height)

$graphicsPath.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$graphicsPath.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$graphicsPath.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$graphicsPath.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$graphicsPath.CloseFigure()

$g.SetClip($graphicsPath)
$g.DrawImage($img, 0, 0, $img.Width, $img.Height)
$g.Dispose()
$img.Dispose()

$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "Rounded corners applied! Dynamic Radius: $radius"
