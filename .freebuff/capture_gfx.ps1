param([int]$ProcId = 5740)
$ps = "$PSScriptRoot"
# 1. Navigate to Graphics
& "$ps\uia_select.ps1" -ProcId $ProcId -Name "Graphics Control" | Out-Null
Start-Sleep -Milliseconds 1200
# 2. Select Overclock tab
& "$ps\uia_select.ps1" -ProcId $ProcId -Name "Overclock" | Out-Null
Start-Sleep -Milliseconds 1000
# 3. Verify via text scan
$txt = & "$ps\uia_textscan.ps1" -ProcId $ProcId -Match "GPU Overclock" 2>$null
if ($txt -notlike "*GPU Overclock*") {
  Write-Warning "page did not render Overclock content; text scan: $txt"
}
Start-Sleep -Milliseconds 300
# 4. Capture
& "$ps\cap3.ps1" $ProcId gfx_overclock3 | Out-Null
Write-Output "captured"
