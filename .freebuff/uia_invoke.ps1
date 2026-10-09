param([int]$ProcId, [string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($el in $els) {
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($p) { $p.Invoke(); Write-Host "invoked: $($el.Current.Name)"; Start-Sleep -Milliseconds 1200; exit 0 }
    } catch { }
}
Write-Host "no invokable element named '$Name'"
