param([int]$ProcId = 5740)
$cap = "$PSScriptRoot\win_gfx_overclock.png"
# settle
Start-Sleep -Milliseconds 600
& "$PSScriptRoot\uia_select.ps1" -ProcId $ProcId -Name "Graphics Control" | Out-Null
Start-Sleep -Milliseconds 700
& "$PSScriptRoot\uia_select.ps1" -ProcId $ProcId -Name "Overclock" | Out-Null
Start-Sleep -Milliseconds 600
# capture
& "$PSScriptRoot\cap3.ps1" $ProcId gfx_overclock2 | Out-Null
Write-Output "done -> $cap"
