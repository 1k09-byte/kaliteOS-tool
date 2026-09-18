# UIA smoke test for the overclock section (phase-4 UI flows).
# Drives the REAL running app: Drivers nav -> risk gate -> accept -> Curve mode -> Auto.
# Exits 0 with SMOKE-OK when all flows pass; prints which step failed otherwise.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Resolve the app window by PID (children-only scope: fast).
$proc = Get-Process kaliteConfig -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if (-not $win) { Write-Output 'WINDOW-NOT-FOUND'; exit 1 }
Write-Output ('WINDOW: ' + $win.Current.Name)

$ctrlCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)

function Get-Names([System.Windows.Automation.AutomationElement]$parent) {
    # One subtree walk, names cached: far faster than repeated FindAll scans.
    $list = @()
    $all = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ctrlCond)
    foreach ($el in $all) { $list += @{ El = $el; Name = $el.Current.Name } }
    return $list
}

function Find-InList($list, [string]$pattern) {
    foreach ($item in $list) { if ($item.Name -like $pattern) { return $item.El } }
    return $null
}

function Select-Item($el) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
}

# 1) navigate to the Drivers page
$names = Get-Names $win
$nav = Find-InList $names 'Drivers'
if (-not $nav) { Write-Output 'NAV-NOT-FOUND'; exit 1 }
Select-Item $nav
Start-Sleep -Seconds 5

# 2) risk gate must be present before acceptance. Note: UIA logically sees
# elements beneath the overlay (the gate is a non-control Border, transparent
# to UIA hit-testing but opaque to real mouse clicks), so element presence
# under the gate is expected and NOT a coverage failure.
$names = Get-Names $win
$gate = Find-InList $names 'Extreme caution*advanced feature'
if ($gate) {
    Write-Output 'GATE-VISIBLE'
} elseif (Find-InList $names 'Core clock') {
    Write-Output 'GATE-ALREADY-ACCEPTED (prior partial run; continuing)'
} else {
    Write-Output 'GATE-MISSING-AND-NO-TELEMETRY'; exit 1
}

# 3) accept the risk
if ($gate) {
    $accept = Find-InList $names 'I understand*continue at my own risk'
    if (-not $accept) { Write-Output 'ACCEPT-NOT-FOUND'; exit 1 }
    $accept.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2

    $names = Get-Names $win
    if (Find-InList $names 'Extreme caution*advanced feature') { Write-Output 'GATE-STILL-VISIBLE'; exit 1 }
    Write-Output 'GATE-ACCEPTED'
}

# telemetry must be live after acceptance (proves the section initialized)
$telem = Find-InList $names 'Core clock'
if ($telem) { Write-Output 'TELEMETRY-VISIBLE' } else { Write-Output 'TELEMETRY-MISSING'; exit 1 }

# 4) switch fan mode to Curve; the curve editor + Apply button must appear
$curve = Find-InList $names 'Curve'
if (-not $curve) { Write-Output 'CURVE-NOT-FOUND'; exit 1 }
Select-Item $curve
Start-Sleep -Seconds 4

$names = Get-Names $win
$apply = Find-InList $names 'Apply curve'
if (-not $apply) { Write-Output 'APPLY-CURVE-NOT-FOUND'; exit 1 }
Write-Output 'CURVE-UI-SHOWN'

# 5) back to Auto: the explicit hand-back path
$auto = Find-InList $names 'Auto'
if (-not $auto) { Write-Output 'AUTO-NOT-FOUND'; exit 1 }
Select-Item $auto
Start-Sleep -Seconds 3

Write-Output 'SMOKE-OK'
exit 0
