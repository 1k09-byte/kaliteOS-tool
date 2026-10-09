$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$PSScriptRoot\paste2.png"))
$uri = "data:image/png;base64,$b64"
$html = @"
<!DOCTYPE html><html><head><meta charset="utf-8"><title>paste2</title>
<style>body{background:#333;color:#eee;font-family:sans-serif;margin:12px} img{border:1px solid #666}</style>
</head><body>
<h3>paste2 full (1871x234)</h3>
<img src="$uri" style="max-width:100%">
<h3>right half 1:1 (x 900+)</h3>
<div style="width:971px;height:234px;overflow:hidden;border:1px solid #666"><img src="$uri" style="margin-left:-900px;max-width:none"></div>
<h3>left half 1:1</h3>
<div style="width:971px;height:234px;overflow:hidden;border:1px solid #666"><img src="$uri" style="max-width:none"></div>
</body></html>
"@
[IO.File]::WriteAllText("$PSScriptRoot\pasteview.html", $html)
Write-Output "bytes=$($html.Length)"
