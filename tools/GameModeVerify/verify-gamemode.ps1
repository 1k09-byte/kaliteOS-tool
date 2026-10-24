$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
}
'@ -IgnoreWarnings

$failures = 0
function Check($label, $cond) {
  if ($cond) { Write-Host "PASS: $label" } else { Write-Host "FAIL: $label"; $script:failures++ }
}

$root = git rev-parse --show-toplevel
$exe = Join-Path $root 'src/kaliteConfig/bin/Debug/net10.0-windows10.0.22621.0/win-x64/kaliteConfig.exe'
if (-not (Test-Path $exe)) { Write-Host "FAIL: build missing: $exe"; exit 1 }

Get-Process kaliteConfig -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Start-Process $exe -ArgumentList '--dev'
$proc = $null
for ($i = 0; $i -lt 60 -and ($proc -eq $null -or $proc.MainWindowHandle -eq [IntPtr]::Zero); $i++) {
  Start-Sleep -Seconds 1
  $proc = Get-Process kaliteConfig -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($proc -eq $null -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no window'; exit 1 }
Write-Host "PID=$($proc.Id)"
[Mouse]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 6
$app = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Get-All() {
  return $app.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
}
function Find-ListItem($name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
  foreach ($el in $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    try { if ($el.Current.Name -eq $name) { return $el } } catch { }
  }
  return $null
}
function Find-Button($name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  foreach ($el in $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    try { if ($el.Current.Name -eq $name) { return $el } } catch { }
  }
  return $null
}
function Snap() {
  $st = @(); $bt = @(); $aff = 0
  foreach ($el in Get-All) {
    try {
      $n = $el.Current.Name
      if ([string]::IsNullOrEmpty($n)) { continue }
      if ($n -eq 'Affinity') { $aff++ }
      elseif ($n -match '^(Idle\.|Game Mode is|Gaming mode|Off$)') { $st += $n }
      elseif ($n -match '(?i)^(Enable|Disable) (Game Mode|Gaming mode)$') { $bt += $n }
    } catch { }
  }
  return @{ Status = ($st -join ' | '); Buttons = ($bt -join ' | '); Affinity = $aff }
}
function Click-El($el) {
  $r = $el.Current.BoundingRectangle
  [Mouse]::SetCursorPos([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2)) | Out-Null
  Start-Sleep -Milliseconds 250
  [Mouse]::mouse_event(0x0002, 0, 0, 0, 0)
  Start-Sleep -Milliseconds 120
  [Mouse]::mouse_event(0x0004, 0, 0, 0, 0)
}

$nav = Find-ListItem 'Process Tuning'
if ($nav -ne $null) { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
Start-Sleep -Seconds 10

$s0 = Snap
Check 'single Game Mode button present' ($s0.Buttons -match '^(En|Dis)able (Game Mode|Gaming mode)$')
Check 'Affinity column present' ($s0.Affinity -ge 2)

$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
  [System.Windows.Automation.ControlType]::ListItem)
$rows = @()
foreach ($el in $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
  try { if ($el.Current.Name -eq 'kaliteConfig.Models.TunerProcessRow') { $rows += $el } } catch { }
}
Check 'process rows loaded' ($rows.Count -ge 5)
if ($rows.Count -ge 5) {
  $rows[4].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Seconds 3
}
$s1 = Snap
Check 'selecting a row does not flip the button' ($s1.Buttons -eq $s0.Buttons)

$b = Find-Button 'Enable Gaming mode'
if ($b -eq $null) { $b = Find-Button 'Disable Gaming mode' }
if ($b -ne $null -and $b.Current.Name -eq 'Disable Gaming mode') {
  Click-El $b; Start-Sleep -Seconds 6
  $b = Find-Button 'Enable Gaming mode'
}
if ($b -ne $null) { Click-El $b; Start-Sleep -Seconds 10 }
$s2 = Snap
Check 'button flips to Disable on enable' ($s2.Buttons -match 'Disable Gaming mode')
Check 'status reports optimize pass' ($s2.Status -match 'background lowered|already on for target|session is already running')

$b2 = Find-Button 'Disable Gaming mode'
if ($b2 -ne $null) { Click-El $b2; Start-Sleep -Seconds 6 }
$s3 = Snap
Check 'button flips back to Enable' ($s3.Buttons -match 'Enable Gaming mode')
Check 'status reports off/restored' ($s3.Status -match 'Game Mode is off|Off')

if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' } else { Write-Host "$failures CHECK(S) FAILED" }
exit $failures
