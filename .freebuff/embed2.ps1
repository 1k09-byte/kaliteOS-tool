$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$PSScriptRoot\paste2.png"))
$uri = "data:image/png;base64,$b64"
$html = @"
<!DOCTYPE html><html><head><meta charset="utf-8"><title>paste2 zoom</title>
<style>body{background:#333;color:#eee;font-family:sans-serif;margin:12px} img{border:1px solid #666}</style>
</head><body>
<h3>Left edge 0-320px, 3x zoom</h3>
<div style="width:960px;height:702px;overflow:hidden;border:1px solid #666"><img src="$uri" style="margin-left:0;margin-top:0;max-width:none;transform:scale(3);transform-origin:0 0"></div>
<h3>x 200-520, 3x zoom</h3>
<div style="width:960px;height:702px;overflow:hidden;border:1px solid #666"><img src="$uri" style="margin-left:-600px;max-width:none;transform:scale(3);transform-origin:0 0"></div>
<h3>x 480-800, 3x zoom</h3>
<div style="width:960px;height:702px;overflow:hidden;border:1px solid #666"><img src="$uri" style="margin-left:-1440px;max-width:none;transform:scale(3);transform-origin:0 0"></div>
</body></html>
"@
[IO.File]::WriteAllText("$PSScriptRoot\pasteview2.html", $html)
Write-Output "bytes=$($html.Length)"
