param([int]$ProcId, [string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($el in $items) {
    $ct = $el.Current.ControlType.ProgrammaticName
    $cn = $el.Current.ClassName
    $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($p) { $p.Select(); Write-Host "selected: $($el.Current.Name) ($ct / $cn)"; Start-Sleep -Milliseconds 800; exit 0 }
    Write-Host "not selectable: $($el.Current.Name) ($ct / $cn)"
}
Write-Host "no selectable item named '$Name' (found $($items.Count) elements)"
