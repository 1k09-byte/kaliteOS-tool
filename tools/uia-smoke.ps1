$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

$names = Get-Names $win
$nav = Find-InList $names 'Drivers'
if (-not $nav) { Write-Output 'NAV-NOT-FOUND'; exit 1 }
Select-Item $nav
Start-Sleep -Seconds 5

$names = Get-Names $win
$gate = Find-InList $names 'Extreme caution*advanced feature'
if ($gate) {
    Write-Output 'GATE-VISIBLE'
} elseif (Find-InList $names 'Core clock') {
    Write-Output 'GATE-ALREADY-ACCEPTED (prior partial run; continuing)'
} else {
    Write-Output 'GATE-MISSING-AND-NO-TELEMETRY'; exit 1
}

if ($gate) {
    $accept = Find-InList $names 'I understand*continue at my own risk'
    if (-not $accept) { Write-Output 'ACCEPT-NOT-FOUND'; exit 1 }
    $accept.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2

    $names = Get-Names $win
    if (Find-InList $names 'Extreme caution*advanced feature') { Write-Output 'GATE-STILL-VISIBLE'; exit 1 }
    Write-Output 'GATE-ACCEPTED'
}

$telem = Find-InList $names 'Core clock'
if ($telem) { Write-Output 'TELEMETRY-VISIBLE' } else { Write-Output 'TELEMETRY-MISSING'; exit 1 }

$curve = Find-InList $names 'Curve'
if (-not $curve) { Write-Output 'CURVE-NOT-FOUND'; exit 1 }
Select-Item $curve
Start-Sleep -Seconds 4

$names = Get-Names $win
$apply = Find-InList $names 'Apply curve'
if (-not $apply) { Write-Output 'APPLY-CURVE-NOT-FOUND'; exit 1 }
Write-Output 'CURVE-UI-SHOWN'

$auto = Find-InList $names 'Auto'
if (-not $auto) { Write-Output 'AUTO-NOT-FOUND'; exit 1 }
Select-Item $auto
Start-Sleep -Seconds 3

Write-Output 'SMOKE-OK'
exit 0
