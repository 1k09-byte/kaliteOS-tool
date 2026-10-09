taskkill //IM kaliteConfig.exe //T //F 2>$null | Out-Null
Start-Sleep -Milliseconds 400
$exe = 'E:\Users\Administrator\source\repos\kaliteconfig\src\kaliteConfig\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\kaliteConfig.exe'
if (!(Test-Path $exe)) { Write-Error "exe not found: $exe"; exit 1 }
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 1400
if ($p.HasExited) { Write-Error "exited with $($p.ExitCode)"; exit 1 }
Write-Output "PID=$($p.Id)"
