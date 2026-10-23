param()
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$exe = Join-Path $root 'src/kaliteConfig/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/kaliteConfig.exe'

function Measure-Idle([string]$Out) {
  & (Join-Path $root 'perf/measure-idle-state.ps1') -Seconds 30 -Out $Out
}

# 1. Fresh start
Get-Process kaliteConfig -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 40

# 2. Idle, visible (home page)
Write-Output ('[visible] ' + (Measure-Idle 'perf/after-idle-visible.csv'))

# 3. Idle, minimized
$p = Get-Process kaliteConfig | Select-Object -First 1
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W2 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
}
"@
[void][W2]::ShowWindow($p.MainWindowHandle, 6)
Start-Sleep -Seconds 5
Write-Output ('[minimized] ' + (Measure-Idle 'perf/after-idle-minimized.csv'))
[void][W2]::ShowWindow($p.MainWindowHandle, 9)
Start-Sleep -Seconds 3

# 4. Visit Graphics Control, then leave the page, then idle again.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process kaliteConfig | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
Add-Type -MemberDefinition $sig -Name F2 -Namespace N2 -ErrorAction SilentlyContinue
[void][N2.F2]::SetForegroundWindow($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
function Select-Nav([string]$name) {
  $rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
  $all = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($el in $all) {
    if ($el.Current.Name -like "*$name*" -and $el.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem') {
      $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
      return $true
    }
  }
  return $false
}
Select-Nav 'Graphics Control' | Out-Null
Start-Sleep -Seconds 6      # page loads, telemetry starts (by design)
Select-Nav 'Power' | Out-Null   # leave the Graphics page
Start-Sleep -Seconds 5      # Teardown -> StopAsync should have halted the loop
Write-Output ('[after-graphics-visit] ' + (Measure-Idle 'perf/after-idle-post-graphics.csv'))
