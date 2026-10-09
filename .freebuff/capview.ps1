param([string]$Cap = "$PSScriptRoot\win_launch.png", [string]$Out = "$PSScriptRoot\capview.html")
$b = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $Cap).Path))
$html = @"
<!DOCTYPE html><html><head><meta charset="utf-8"><title>capture</title>
<style>body{background:#222;color:#eee;font-family:sans-serif;margin:12px} img{border:1px solid #666;max-width:100%}</style>
</head><body><h3>$(Split-Path $Cap -Leaf)</h3><img src="data:image/png;base64,$b"></body></html>
"@
[IO.File]::WriteAllText($Out, $html)
Write-Output "wrote $Out  ($($html.Length) bytes)"
