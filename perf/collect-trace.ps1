param(
  [string]$Exe = "src/kaliteConfig/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/kaliteConfig.exe",
  [int]$SettleSeconds = 45,
  [int]$TraceSeconds = 25,
  [string]$OutDir = "perf"
)
$ErrorActionPreference = "Stop"

$ExeFull = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Exe))
if (!(Test-Path $ExeFull)) { Write-Output "EXE-NOT-FOUND $ExeFull"; exit 1 }
$traceTool = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) "perf/tools/dotnet-trace.exe"))
if (!(Test-Path $traceTool)) { Write-Output "TRACE-TOOL-NOT-FOUND $traceTool"; exit 1 }

# Fresh start so startup work is behind us and state is deterministic.
Get-Process kaliteConfig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$p = Start-Process -FilePath $ExeFull -PassThru
Start-Sleep -Seconds $SettleSeconds
$p.Refresh()
if ($p.HasExited) { Write-Output "APP-EXITED-AFTER-START"; exit 1 }

Write-Output ("visible: pid={0} start cpu ms={1}" -f $p.Id, $p.TotalProcessorTime.TotalMilliseconds)
& $traceTool collect --process-id $p.Id --duration ("00:00:{0:D2}" -f $TraceSeconds) --output "$OutDir/idle-visible.nettrace"
if ($LASTEXITCODE -ne 0) { Write-Output "TRACE-VISIBLE-FAILED exit=$LASTEXITCODE"; exit 1 }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Show {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
"@
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Output "NO-WINDOW-HANDLE"; exit 1 }
[Win32Show]::ShowWindow($h, 6) | Out-Null   # SW_MINIMIZE
Start-Sleep -Seconds 8
if (![Win32Show]::IsIconic($h)) { Write-Output "MINIMIZE-FAILED"; exit 1 }

$p.Refresh()
Write-Output ("minimized: cpu ms={0}" -f $p.TotalProcessorTime.TotalMilliseconds)
& $traceTool collect --process-id $p.Id --duration ("00:00:{0:D2}" -f $TraceSeconds) --output "$OutDir/idle-minimized.nettrace"
if ($LASTEXITCODE -ne 0) { Write-Output "TRACE-MINIMIZED-FAILED exit=$LASTEXITCODE"; exit 1 }

[Win32Show]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE
Write-Output "TRACE-COLLECTION-DONE"
