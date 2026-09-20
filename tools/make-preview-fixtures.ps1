<#
  Generates the snips needed to walk through Docs/SnipPreview.md's manual checklist, straight into
  the gallery folder (Pictures\kaliteConfig Snips) with a SNIPTEST_ prefix.

    powershell -ExecutionPolicy Bypass -File tools\make-preview-fixtures.ps1
    powershell -ExecutionPolicy Bypass -File tools\make-preview-fixtures.ps1 -Cleanup

  What each fixture is for:
    4000x3000 PNG  - 12 Mpx, direct path: placeholder -> full-res crossfade, 100% alignment,
                     interpolation (1 px and 2 px checkers, 1 px rules, 100 px ticks).
    8500x1500 PNG  - 12.75 Mpx but an 8500 px longest edge: the CanvasVirtualBitmap (region
                     on demand) path via the edge rule.
    7680x4320 JPG  - 33 Mpx, the realistic 8K case: the pixel-count rule, memory must stay flat.

  Everything is a plain, deterministic pattern: no noise, no external assets.
#>
param(
    [switch]$Cleanup
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$gallery = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'kaliteConfig Snips'
if (-not (Test-Path $gallery)) { New-Item -ItemType Directory -Path $gallery | Out-Null }

if ($Cleanup) {
    Get-ChildItem -Path $gallery -Filter 'SNIPTEST_*' | ForEach-Object {
        Write-Host "removing $($_.Name)"
        Remove-Item $_.FullName -Force
    }
    Write-Host 'fixtures removed.'
    return
}

function New-Fixture {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [int] $Width,
        [Parameter(Mandatory)] [int] $Height,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Subtitle,
        [Parameter(Mandatory)] [bool] $Jpeg
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor

        # 1. Smooth backdrop: any quality problem (banding, blur) shows up instantly here.
        $rect = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
        $backdrop = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(16, 24, 40), [System.Drawing.Color]::FromArgb(240, 236, 224), 20.0)
        try { $graphics.FillRectangle($backdrop, $rect) } finally { $backdrop.Dispose() }

        # 2. Solid corner blocks: their edges are the 100% alignment reference.
        $blockW = [Math]::Min(140, [int]($Width / 8)); $blockH = [Math]::Min(140, [int]($Height / 8))
        $graphics.FillRectangle([System.Drawing.Brushes]::Red, $Width - $blockW, 0, $blockW, $blockH)
        $graphics.FillRectangle([System.Drawing.Brushes]::Lime, 0, $Height - $blockH, $blockW, $blockH)
        $graphics.FillRectangle([System.Drawing.Brushes]::Blue, $Width - $blockW, $Height - $blockH, $blockW, $blockH)
        $graphics.FillRectangle([System.Drawing.Brushes]::White, 0, 0, $blockW, $blockH)

        # 3. Checkers: 1 px and 2 px tiles. At >=200% zoom these must be crisp squares; below 100%
        #    the averaged result must be flat grey (never aliased noise).
        $checker2 = New-Object System.Drawing.Bitmap(2, 2)
        $checker2.SetPixel(0, 0, [System.Drawing.Color]::Black); $checker2.SetPixel(1, 1, [System.Drawing.Color]::Black)
        $checker2.SetPixel(1, 0, [System.Drawing.Color]::White); $checker2.SetPixel(0, 1, [System.Drawing.Color]::White)
        $checker4 = New-Object System.Drawing.Bitmap(4, 4)
        for ($y = 0; $y -lt 4; $y++) {
            for ($x = 0; $x -lt 4; $x++) {
                $on = ([int](($x / 2) + ($y / 2)) % 2) -eq 0
                $checker4.SetPixel($x, $y, $(if ($on) { [System.Drawing.Color]::Black } else { [System.Drawing.Color]::White }))
            }
        }
        $patchW = 160; $patchH = 160
        foreach ($pair in @(@($checker2, 200), @($checker4, 400))) {
            $brush = New-Object System.Drawing.TextureBrush($pair[0])
            $brush.WrapMode = [System.Drawing.Drawing2D.WrapMode]::Tile
            try { $graphics.FillRectangle($brush, [int]$pair[1], 40, $patchW, $patchH) } finally { $brush.Dispose() }
            $text = if ($pair[0].Width -eq 2) { '1 px checker' } else { '2 px checker' }
            $graphics.DrawString($text, (New-Object System.Drawing.Font('Segoe UI', 14)), [System.Drawing.Brushes]::White, [int]$pair[1], 204)
        }
        $checker2.Dispose(); $checker4.Dispose()

        # 4. 100 px ticks with 500 px labels, plus 1 px rules: count pixels to confirm 100% is 1:1.
        $tickPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 210, 60), 1)
        $rulePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0, 200, 255), 1)
        $tickFont = New-Object System.Drawing.Font('Consolas', 12)
        for ($x = 0; $x -lt $Width; $x += 100) {
            $tall = ($x % 500) -eq 0
            $graphics.DrawLine($tickPen, $x, 0, $x, $(if ($tall) { 30 } else { 12 }))
            if ($tall -and $x -gt 0) { $graphics.DrawString("x=$x", $tickFont, [System.Drawing.Brushes]::White, $x + 4, 32) }
        }
        for ($y = 0; $y -lt $Height; $y += 100) {
            $graphics.DrawLine($tickPen, 0, $y, 12, $y)
            if (($y % 500) -eq 0) { $graphics.DrawString("y=$y", $tickFont, [System.Drawing.Brushes]::White, 16, $y + 2) }
        }
        $graphics.DrawLine($rulePen, 0, [int]($Height / 2) + 30, $Width, [int]($Height / 2) + 30)   # 1 px horizontal rule
        $graphics.DrawLine($rulePen, [int]($Width / 2) + 30, 0, [int]($Width / 2) + 30, $Height)   # 1 px vertical rule
        $graphics.DrawLine($rulePen, 600, 300, 1400, 700)                                          # 1 px diagonal

        # 5. A 4x4 px block at a known offset: at 400% it must be a 16x16 screen-pixel square.
        $graphics.FillRectangle([System.Drawing.Brushes]::Magenta, 800, 300, 4, 4)

        # 6. Labels.
        $titleFont = New-Object System.Drawing.Font('Segoe UI', 40, [System.Drawing.FontStyle]::Bold)
        $subFont = New-Object System.Drawing.Font('Segoe UI', 22)
        $graphics.DrawString($Label, $titleFont, [System.Drawing.Brushes]::White, 40, $Height - 200)
        $graphics.DrawString($Subtitle, $subFont, [System.Drawing.Brushes]::White, 40, $Height - 130)
        $graphics.DrawString('red top-right / lime bottom-left / blue bottom-right / magenta 4x4 px block at 800,300', (New-Object System.Drawing.Font('Segoe UI', 16)), [System.Drawing.Brushes]::White, 40, $Height - 80)
    }
    finally {
        $graphics.Dispose()
    }

    $path = Join-Path $gallery $Name
    if ($Jpeg) {
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
        $parameters = New-Object System.Drawing.Imaging.EncoderParameters(1)
        $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 82L)
        $bitmap.Save($path, $codec, $parameters)
        $parameters.Param[0].Dispose(); $parameters.Dispose()
    }
    else {
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bitmap.Dispose()

    $size = [Math]::Round((Get-Item $path).Length / 1MB, 2)
    Write-Host ("wrote {0}  ({1} x {2}, {3} MB)" -f $Name, $Width, $Height, $size)
}

New-Fixture -Name 'SNIPTEST_checker_4000x3000.png' -Width 4000 -Height 3000 `
    -Label '4000 x 3000' -Subtitle 'direct path: crossfade + 100% alignment + interpolation' -Jpeg $false

New-Fixture -Name 'SNIPTEST_huge_8500x1500.png' -Width 8500 -Height 1500 `
    -Label '8500 x 1500' -Subtitle 'longest edge > 8192: CanvasVirtualBitmap regions' -Jpeg $false

New-Fixture -Name 'SNIPTEST_8k_7680x4320.jpg' -Width 7680 -Height 4320 `
    -Label '7680 x 4320 (8K)' -Subtitle '33 Mpx: virtual bitmap, memory must stay flat' -Jpeg $true

Write-Host ''
Write-Host "fixtures are in: $gallery"
Write-Host 're-run with -Cleanup to remove them.'
